using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Integration.Hubs
{
    // El chat de punta a punta: navegador → SignalR → StreamHub → ChatService → difusión.
    //
    // Para comprobar que algo NO le llegó a alguien se usa una "barrera": después se
    // manda otro mensaje que ese cliente SÍ debe recibir. SignalR entrega en orden por
    // conexión, así que cuando llega la barrera, lo anterior ya habría llegado.
    [TestClass]
    public class HubChatTests
    {
        private AppUnderTest _app = null!;

        [TestInitialize]
        public async Task Start() => _app = await AppUnderTest.StartAsync();

        [TestCleanup]
        public async Task Stop() => await _app.DisposeAsync();

        [TestMethod]
        public async Task MensajeValido_LesLlegaATodosLosDelCanal_IncluidoQuienLoEscribio()
        {
            var writer = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            var reader = await _app.JoinAsync(Demo.Channel1, Demo.Viewer2);
            var anonymous = await _app.JoinAsync(Demo.Channel1);

            var result = await writer.SendAsync(Demo.Channel1, "hola pepega");

            Assert.IsTrue(result.Ok, result.ErrorMessage);
            foreach (var client in new[] { writer, reader, anonymous })
            {
                var message = await client.WaitForChatAsync("hola pepega");
                Assert.AreEqual(Demo.Viewer1, message.UserId);
                Assert.AreEqual(Demo.Viewer1Name, message.DisplayName);
                Assert.AreEqual(Demo.Channel1, message.ChannelId);
                Assert.AreEqual(2, message.Fragments.Count);
                Assert.AreEqual(ChatFragment.EmoteType, message.Fragments[1].Type);
                Assert.AreEqual("/emotes/1_pepega.png", message.Fragments[1].EmoteUrl);
            }
            Assert.AreEqual(1, _app.Db.MessagesOf(Demo.Channel1).Count, "Queda guardado para el historial.");
        }

        [TestMethod]
        public async Task Anonimo_PuedeLeerElChatPeroNoEscribir()
        {
            var anonymous = await _app.ConnectAsync();
            var join = await anonymous.JoinAsync(Demo.Channel1);
            Assert.IsFalse(join.CanModerate);

            var error = await Assert.ThrowsExceptionAsync<HubException>(() => anonymous.SendAsync(Demo.Channel1, "hola"));

            StringAssert.Contains(error.Message, "unauthorized");
            Assert.AreEqual(0, _app.Db.MessagesOf(Demo.Channel1).Count);
        }

        [TestMethod]
        public async Task NoSePuedeSuplantarAOtroUsuario_MandandoSuId()
        {
            var attacker = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);

            // El hub solo acepta (canal, texto, respuestaA): quien escribe sale SIEMPRE del login.
            await Assert.ThrowsExceptionAsync<HubException>(() =>
                attacker.Connection.InvokeAsync<object>("SendMessage", Demo.Channel1, "soy el dueño", null, Demo.Owner1));

            // Meter el id del dueño en "respuestaA" tampoco sirve: no es un mensaje, así que se ignora.
            var result = await attacker.SendAsync(Demo.Channel1, "hola", replyToId: Demo.Owner1);
            Assert.IsTrue(result.Ok, result.ErrorMessage);
            Assert.AreEqual(Demo.Viewer1, result.Message!.UserId);
            Assert.IsNull(result.Message.ReplyTo);
        }

        [TestMethod]
        public async Task LosMensajes_NoCruzanDeUnCanalAOtro()
        {
            var inChannel1 = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            var inChannel2 = await _app.JoinAsync(Demo.Channel2, Demo.Viewer2);

            await inChannel1.SendAsync(Demo.Channel1, "solo para el canal 1");
            await inChannel2.SendAsync(Demo.Channel2, "barrera canal 2");

            await inChannel2.WaitForChatAsync("barrera canal 2");
            Assert.AreEqual(1, inChannel2.ChatMessages.Count, "En el canal 2 no debe aparecer el mensaje del canal 1.");
            await inChannel1.WaitForChatAsync("solo para el canal 1");
            Assert.AreEqual(1, inChannel1.ChatMessages.Count);
        }

        [TestMethod]
        public async Task MensajeRechazado_SoloQuienEscribioVeElMotivo_NoSeDifunde()
        {
            await _app.DisposeAsync();
            _app = await AppUnderTest.StartAsync(arrange: db => db.FilterFor(Demo.Channel1).BannedWords.Add("spam"));

            var writer = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            var reader = await _app.JoinAsync(Demo.Channel1, Demo.Viewer2);

            var result = await writer.SendAsync(Demo.Channel1, "compren SPAM barato");

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("blocked_term", result.ErrorCode);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.ErrorMessage));

            await reader.SendAsync(Demo.Channel1, "barrera");
            await writer.WaitForChatAsync("barrera");
            await reader.WaitForChatAsync("barrera");
            Assert.AreEqual(1, reader.ChatMessages.Count);
            Assert.AreEqual(1, writer.ChatMessages.Count);
        }

        [TestMethod]
        public async Task FiltrosDeMayusculasYRepetidos_RechazanSinDifundir()
        {
            await _app.DisposeAsync();
            _app = await AppUnderTest.StartAsync(arrange: db =>
            {
                db.FilterFor(Demo.Channel1).BlockExcessiveCaps = true;
                db.FilterFor(Demo.Channel1).BlockRepeatedChars = true;
            });

            var writer = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            var reader = await _app.JoinAsync(Demo.Channel1, Demo.Viewer2);

            Assert.AreEqual("too_many_caps", (await writer.SendAsync(Demo.Channel1, "ESTO ES UN GRITO ENORME")).ErrorCode);
            Assert.AreEqual("repeated_chars", (await writer.SendAsync(Demo.Channel1, "holaaaaaaaaa")).ErrorCode);

            await reader.SendAsync(Demo.Channel1, "barrera");
            await reader.WaitForChatAsync("barrera");
            Assert.AreEqual(1, reader.ChatMessages.Count, "Ninguno de los dos mensajes rechazados se difundió.");
        }

        [TestMethod]
        public async Task ModoLento_SeAplicaPorUsuarioAtravesDelHub()
        {
            await _app.DisposeAsync();
            _app = await AppUnderTest.StartAsync(arrange: db =>
            {
                db.FilterFor(Demo.Channel1).SlowModeEnabled = true;
                db.FilterFor(Demo.Channel1).SlowModeSeconds = 30;
            });

            var viewer = await _app.ConnectAsync(Demo.Viewer1);
            var join = await viewer.JoinAsync(Demo.Channel1);
            Assert.AreEqual(30, join.Settings.SlowModeSeconds, "El navegador recibe el modo lento al entrar.");

            Assert.IsTrue((await viewer.SendAsync(Demo.Channel1, "uno")).Ok);
            var second = await viewer.SendAsync(Demo.Channel1, "dos");
            Assert.AreEqual("slow_mode", second.ErrorCode);
            Assert.AreEqual(30, second.RetryAfterSeconds);

            _app.Db.Clock.AdvanceSeconds(30);
            Assert.IsTrue((await viewer.SendAsync(Demo.Channel1, "tres")).Ok);
        }

        [TestMethod]
        public async Task HtmlEnElMensaje_ViajaComoTextoPlano()
        {
            var writer = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            const string html = "<img src=x onerror=alert(1)>";

            await writer.SendAsync(Demo.Channel1, html);

            var message = await writer.WaitForChatAsync(html);
            Assert.AreEqual(1, message.Fragments.Count);
            Assert.AreEqual(ChatFragment.TextType, message.Fragments[0].Type);
            Assert.IsNull(message.Fragments[0].EmoteUrl);
        }

        [TestMethod]
        public async Task CuentaSuspendida_NoPuedeEscribir()
        {
            var suspended = await _app.JoinAsync(Demo.Channel1, Demo.Suspended);

            var result = await suspended.SendAsync(Demo.Channel1, "hola");

            Assert.AreEqual("suspended", result.ErrorCode);
        }

        [TestMethod]
        public async Task EntrarAUnCanalQueNoExiste_DaUnErrorClaro()
        {
            var client = await _app.ConnectAsync(Demo.Viewer1);

            var error = await Assert.ThrowsExceptionAsync<HubException>(() => client.JoinAsync(Demo.MissingChannel));

            StringAssert.Contains(error.Message, "El canal no existe.");
        }

        [TestMethod]
        public async Task AlEntrar_ElNavegadorSabeSiPuedeModerar()
        {
            Assert.IsTrue((await (await _app.ConnectAsync(Demo.Owner1)).JoinAsync(Demo.Channel1)).CanModerate, "Dueño del canal");
            Assert.IsTrue((await (await _app.ConnectAsync(Demo.Admin, "Admin")).JoinAsync(Demo.Channel1)).CanModerate, "Admin");
            Assert.IsFalse((await (await _app.ConnectAsync(Demo.Admin)).JoinAsync(Demo.Channel1)).CanModerate, "El mismo usuario SIN el rol Admin");
            Assert.IsFalse((await (await _app.ConnectAsync(Demo.Owner2)).JoinAsync(Demo.Channel1)).CanModerate, "Dueño de OTRO canal");
            Assert.IsFalse((await (await _app.ConnectAsync(Demo.Viewer1)).JoinAsync(Demo.Channel1)).CanModerate, "Viewer");
            Assert.IsFalse((await (await _app.ConnectAsync()).JoinAsync(Demo.Channel1)).CanModerate, "Anónimo");
        }
    }
}
