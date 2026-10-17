using Microsoft.AspNetCore.SignalR;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Integration.Hubs
{
    // Moderación desde el chat: qué evento le llega a quién.
    [TestClass]
    public class HubModerationTests
    {
        private AppUnderTest _app = null!;
        private HubClient _owner = null!;
        private HubClient _troll = null!;
        private HubClient _bystander = null!;

        [TestInitialize]
        public async Task Start()
        {
            _app = await AppUnderTest.StartAsync();
            _owner = await _app.JoinAsync(Demo.Channel1, Demo.Owner1);
            _troll = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            _bystander = await _app.JoinAsync(Demo.Channel1, Demo.Viewer2);
        }

        [TestCleanup]
        public async Task Stop() => await _app.DisposeAsync();

        [TestMethod]
        public async Task Ban_TodosVenElAvisoYAlBaneadoLeLlegaSuEvento()
        {
            var result = await _owner.ModerateAsync("BanUser", Demo.Channel1, Demo.Viewer1, "spam");

            Assert.IsTrue(result.Ok, result.Error);
            var notice = await _bystander.WaitForAsync("UserBanned");
            Assert.AreEqual(Demo.Viewer1, notice.Arg<string>(0));
            Assert.AreEqual(Demo.Viewer1Name, notice.Arg<string>(1));

            var personal = await _troll.WaitForAsync("Banned");
            Assert.AreEqual(Demo.Channel1, personal.Arg<int>(0));

            Assert.AreEqual("banned", (await _troll.SendAsync(Demo.Channel1, "por qué?")).ErrorCode);
        }

        [TestMethod]
        public async Task Timeout_LeLlegaAlSancionadoEnTodasSusPestanas()
        {
            // Una segunda pestaña del mismo usuario que todavía no entró a ningún canal.
            var otherTab = await _app.ConnectAsync(Demo.Viewer1);

            var result = await _owner.ModerateAsync("TimeoutUser", Demo.Channel1, Demo.Viewer1, 60);

            Assert.IsTrue(result.Ok, result.Error);
            var notice = await _bystander.WaitForAsync("UserTimedOut");
            Assert.AreEqual(Demo.Viewer1, notice.Arg<string>(0));
            Assert.AreEqual(60, notice.Arg<int>(2));

            foreach (var tab in new[] { _troll, otherTab })
            {
                var personal = await tab.WaitForAsync("TimedOut");
                Assert.AreEqual(Demo.Channel1, personal.Arg<int>(0));
                Assert.AreEqual(60, personal.Arg<int>(1));
            }
            Assert.AreEqual(0, otherTab.Received("UserTimedOut").Count, "El aviso público es solo para quienes están en el canal.");
        }

        [TestMethod]
        public async Task BorrarMensaje_DesapareceParaTodos()
        {
            var messageId = (await _troll.SendAsync(Demo.Channel1, "mensaje feo")).Message!.Id;

            var result = await _owner.ModerateAsync("DeleteMessage", Demo.Channel1, messageId);

            Assert.IsTrue(result.Ok, result.Error);
            foreach (var client in new[] { _owner, _troll, _bystander })
            {
                var deleted = await client.WaitForAsync("MessageDeleted");
                Assert.AreEqual(messageId, deleted.Arg<string>(0));
            }
            Assert.AreEqual(0, _app.Db.MessagesOf(Demo.Channel1).Count);
        }

        [TestMethod]
        public async Task LimpiarChat_SoloAfectaAEseCanal()
        {
            var otherChannel = await _app.JoinAsync(Demo.Channel2, Demo.Viewer3);
            await _troll.SendAsync(Demo.Channel1, "a");
            await otherChannel.SendAsync(Demo.Channel2, "b");

            var result = await _owner.ModerateAsync("ClearChat", Demo.Channel1);

            Assert.IsTrue(result.Ok, result.Error);
            await _bystander.WaitForAsync("ChatCleared");

            await otherChannel.SendAsync(Demo.Channel2, "barrera");
            await otherChannel.WaitForChatAsync("barrera");
            Assert.AreEqual(0, otherChannel.Received("ChatCleared").Count);
            Assert.AreEqual(2, _app.Db.MessagesOf(Demo.Channel2).Count);
        }

        [TestMethod]
        public async Task Unban_ElUsuarioRecibeElAvisoYPuedeVolverAEscribir()
        {
            await _owner.ModerateAsync("BanUser", Demo.Channel1, Demo.Viewer1, "spam");

            var result = await _owner.ModerateAsync("UnbanUser", Demo.Channel1, Demo.Viewer1);

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual(Demo.Channel1, (await _troll.WaitForAsync("Unbanned")).Arg<int>(0));
            Assert.IsTrue((await _troll.SendAsync(Demo.Channel1, "perdón")).Ok);
        }

        [TestMethod]
        public async Task UnViewer_NoPuedeModerar_YNoSeDifundeNada()
        {
            var result = await _troll.ModerateAsync("BanUser", Demo.Channel1, Demo.Viewer2, "porque sí");

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("No tienes permisos de moderación en este canal.", result.Error);

            await _troll.SendAsync(Demo.Channel1, "barrera");
            await _bystander.WaitForChatAsync("barrera");
            Assert.AreEqual(0, _bystander.Received("UserBanned").Count);
            Assert.AreEqual(0, _bystander.Received("Banned").Count);
            Assert.AreEqual(0, _app.Db.Bans.Count);
        }

        [TestMethod]
        public async Task UnAnonimo_NiSiquieraPuedeLlamarLosMetodosDeModeracion()
        {
            var anonymous = await _app.JoinAsync(Demo.Channel1);

            var error = await Assert.ThrowsExceptionAsync<HubException>(() =>
                anonymous.ModerateAsync("ClearChat", Demo.Channel1));

            StringAssert.Contains(error.Message, "unauthorized");
        }

        [TestMethod]
        public async Task UnAdmin_ModeraCualquierCanal()
        {
            var admin = await _app.JoinAsync(Demo.Channel1, Demo.Admin, "Admin");

            var result = await admin.ModerateAsync("TimeoutUser", Demo.Channel1, Demo.Viewer1, 300);

            Assert.IsTrue(result.Ok, result.Error);
            await _troll.WaitForAsync("TimedOut");
        }

        [TestMethod]
        public async Task AutoMod_SoloLosModeradoresVenLoRetenido_YAlAprobarLoVenTodos()
        {
            await _app.DisposeAsync();
            _app = await AppUnderTest.StartAsync(arrange: db =>
            {
                db.FilterFor(Demo.Channel1).AutoModEnabled = true;
                db.AutoMod.FlagWhenContains("insulto", "tonto");
            });
            var owner = await _app.JoinAsync(Demo.Channel1, Demo.Owner1);
            var writer = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            var bystander = await _app.JoinAsync(Demo.Channel1, Demo.Viewer2);

            var sent = await writer.SendAsync(Demo.Channel1, "qué tonto ese jefe");
            Assert.IsTrue(sent.HeldForReview);

            var held = (await owner.WaitForAsync("AutoModHeld")).Arg<TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs.AutoModHeldDto>(0);
            Assert.AreEqual("qué tonto ese jefe", held.Text);

            var resolved = await owner.ModerateAsync("ResolveAutoMod", Demo.Channel1, held.Id, true);
            Assert.IsTrue(resolved.Ok, resolved.Error);

            await bystander.WaitForChatAsync("qué tonto ese jefe");
            Assert.AreEqual(0, bystander.Received("AutoModHeld").Count, "Un viewer nunca ve la cola de AutoMod.");
            await owner.WaitForAsync("AutoModResolved");
        }

        [TestMethod]
        public async Task ElDuenoDelCanal_NoPuedeSerSancionado_NiPorUnAdmin()
        {
            var admin = await _app.JoinAsync(Demo.Channel1, Demo.Admin, "Admin");

            var result = await admin.ModerateAsync("BanUser", Demo.Channel1, Demo.Owner1, "x");

            Assert.IsFalse(result.Ok);
            Assert.IsTrue((await _owner.SendAsync(Demo.Channel1, "sigo aquí")).Ok);
        }
    }
}
