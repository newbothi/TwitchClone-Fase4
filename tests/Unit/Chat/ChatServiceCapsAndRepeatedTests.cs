using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Chat
{
    // Fase 4 (a): filtros de mayúsculas y caracteres repetidos dentro del pipeline del chat.
    [TestClass]
    public class ChatServiceCapsAndRepeatedTests
    {
        private const string Shout = "ESTO ES UN GRITO ENORME";
        private const string Stretched = "holaaaaaaaa a todos";

        private ChatTestContext _ctx = null!;

        [TestInitialize]
        public void Setup() => _ctx = new ChatTestContext();

        private void EnableBoth() => _ctx.ConfigureChannel(Demo.Channel1, f =>
        {
            f.BlockExcessiveCaps = true;
            f.BlockRepeatedChars = true;
        });

        [TestMethod]
        public async Task PorDefecto_EstanDesactivados()
        {
            Assert.IsTrue((await _ctx.Send(As.Viewer1, Shout)).Ok);
            Assert.IsTrue((await _ctx.Send(As.Viewer2, Stretched)).Ok);
        }

        [TestMethod]
        public async Task Activados_RechazanConUnMotivoClaro()
        {
            EnableBoth();

            var caps = await _ctx.Send(As.Viewer1, Shout);
            var repeated = await _ctx.Send(As.Viewer2, Stretched);

            Assert.AreEqual("too_many_caps", caps.ErrorCode);
            StringAssert.Contains(caps.ErrorMessage, "mayúsculas");
            Assert.AreEqual("repeated_chars", repeated.ErrorCode);
            StringAssert.Contains(repeated.ErrorMessage, "7");
            Assert.AreEqual(0, _ctx.Db.MessagesOf(Demo.Channel1).Count, "No se guarda nada.");
        }

        [TestMethod]
        public async Task CadaFiltroSeActivaPorSeparado()
        {
            _ctx.ConfigureChannel(Demo.Channel1, f => f.BlockExcessiveCaps = true);

            Assert.AreEqual("too_many_caps", (await _ctx.Send(As.Viewer1, Shout)).ErrorCode);
            Assert.IsTrue((await _ctx.Send(As.Viewer2, Stretched)).Ok, "Los repetidos siguen permitidos.");
        }

        [TestMethod]
        public async Task LosEmotesEnMayusculasNoCuentan()
        {
            _ctx.Db.AddEmote(Demo.Channel1, "KEKW", "/emotes/1_kekw.png");
            _ctx.Db.AddEmote(Demo.Channel1, "POGGERS", "/emotes/1_poggers.png");
            EnableBoth();

            var result = await _ctx.Send(As.Viewer1, "KEKW POGGERS KEKW que jugada");

            Assert.IsTrue(result.Ok, result.ErrorMessage);
        }

        [TestMethod]
        public async Task VipModeradoresYDuenoQuedanExentos()
        {
            EnableBoth();
            _ctx.MakeVip(Demo.Viewer2);
            _ctx.MakeModerator(Demo.Viewer3);

            Assert.IsTrue((await _ctx.Send(As.Viewer2, Shout)).Ok, "VIP");
            Assert.IsTrue((await _ctx.Send(As.Viewer3, Stretched)).Ok, "Moderador");
            Assert.IsTrue((await _ctx.Send(As.Owner, Shout)).Ok, "Dueño");
            Assert.IsTrue((await _ctx.Send(As.Admin, Stretched)).Ok, "Admin");
        }

        [TestMethod]
        public async Task UnMensajeRechazado_NoGastaElModoLento()
        {
            EnableBoth();
            _ctx.ConfigureChannel(Demo.Channel1, f => { f.SlowModeEnabled = true; f.SlowModeSeconds = 30; });

            Assert.AreEqual("too_many_caps", (await _ctx.Send(As.Viewer1, Shout)).ErrorCode);
            var corrected = await _ctx.Send(As.Viewer1, "esto es un grito enorme");

            Assert.IsTrue(corrected.Ok, "Puede corregir el mensaje al tiro, sin esperar los 30 s.");
        }

        [TestMethod]
        public async Task SeGuardanYSeLeenDesdeElDashboard()
        {
            await _ctx.Chat.UpdateSettingsAsync(Demo.Channel1, new ChatModerationSettingsDto
            {
                BlockExcessiveCaps = true,
                BlockRepeatedChars = true
            });

            var settings = await _ctx.Chat.GetModerationSettingsAsync(Demo.Channel1);
            Assert.IsTrue(settings.BlockExcessiveCaps);
            Assert.IsTrue(settings.BlockRepeatedChars);
            Assert.AreEqual("too_many_caps", (await _ctx.Send(As.Viewer1, Shout)).ErrorCode, "Rige al instante.");
        }

        [TestMethod]
        public async Task LosComandosDeModeracion_NoLosDesactivan()
        {
            EnableBoth();

            // /slow cambia solo el modo lento: los filtros nuevos deben seguir activos.
            var slow = await _ctx.Send(As.Owner, "/slow 5");
            Assert.IsTrue(slow.Ok, slow.ErrorMessage);
            _ctx.Clock.AdvanceSeconds(10);

            Assert.AreEqual("too_many_caps", (await _ctx.Send(As.Viewer1, Shout)).ErrorCode);
            Assert.AreEqual("repeated_chars", (await _ctx.Send(As.Viewer2, Stretched)).ErrorCode);
        }
    }
}
