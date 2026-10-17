using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Integration.Hubs
{
    // Contador de espectadores en vivo (PresenceTracker + PresenceBroadcaster reales).
    [TestClass]
    public class HubPresenceTests
    {
        private AppUnderTest _app = null!;

        [TestInitialize]
        public async Task Start() => _app = await AppUnderTest.StartAsync();

        [TestCleanup]
        public async Task Stop() => await _app.DisposeAsync();

        [TestMethod]
        public async Task VariasPestanasDelMismoUsuario_CuentanComoUnEspectador()
        {
            Assert.AreEqual(1, (await (await _app.ConnectAsync(Demo.Viewer1)).JoinAsync(Demo.Channel1)).ViewerCount);
            Assert.AreEqual(1, (await (await _app.ConnectAsync(Demo.Viewer1)).JoinAsync(Demo.Channel1)).ViewerCount, "Segunda pestaña");
            Assert.AreEqual(2, (await (await _app.ConnectAsync()).JoinAsync(Demo.Channel1)).ViewerCount, "Un anónimo");
            Assert.AreEqual(3, (await (await _app.ConnectAsync()).JoinAsync(Demo.Channel1)).ViewerCount, "Otro anónimo");
        }

        [TestMethod]
        public async Task ListaDeEspectadores_NombresOrdenadosMasAnonimos()
        {
            await _app.JoinAsync(Demo.Channel1, Demo.Viewer2);
            await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            await _app.JoinAsync(Demo.Channel1);
            var asker = await _app.JoinAsync(Demo.Channel1, Demo.Viewer3);

            var list = await asker.GetViewersAsync(Demo.Channel1);

            Assert.AreEqual(4, list.Count);
            CollectionAssert.AreEqual(new[] { Demo.Viewer2Name, Demo.Viewer3Name, Demo.Viewer1Name }, list.Names);
            Assert.AreEqual(1, list.AnonymousCount);
        }

        [TestMethod]
        public async Task CuandoAlguienEntra_ElContadorSeActualizaParaTodos()
        {
            var first = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);

            await _app.JoinAsync(Demo.Channel1, Demo.Viewer2);

            // PresenceBroadcaster difunde cada 2 s.
            await first.WaitForAsync("ViewerCountUpdated", e => e.Arg<int>(0) == 2);
        }

        [TestMethod]
        public async Task CuandoAlguienCierraLaPestana_ElContadorBaja()
        {
            var stays = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            var leaves = await _app.JoinAsync(Demo.Channel1, Demo.Viewer2);
            await stays.WaitForAsync("ViewerCountUpdated", e => e.Arg<int>(0) == 2);

            await leaves.Connection.StopAsync();

            await stays.WaitForAsync("ViewerCountUpdated", e => e.Arg<int>(0) == 1);
            Assert.AreEqual(1, (await stays.GetViewersAsync(Demo.Channel1)).Count);
        }

        [TestMethod]
        public async Task CambiarDeCanal_DejaDeRecibirElChatDelAnterior()
        {
            var zapper = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            var stays = await _app.JoinAsync(Demo.Channel1, Demo.Viewer2);

            var join = await zapper.JoinAsync(Demo.Channel2);
            Assert.AreEqual(1, join.ViewerCount);
            Assert.AreEqual(1, (await stays.GetViewersAsync(Demo.Channel1)).Count, "Salió del canal 1.");

            await stays.SendAsync(Demo.Channel1, "mensaje en el canal 1");
            await zapper.SendAsync(Demo.Channel2, "barrera canal 2");
            await zapper.WaitForChatAsync("barrera canal 2");
            Assert.AreEqual(1, zapper.ChatMessages.Count, "Ya no está en el grupo de SignalR del canal 1.");
        }

        [TestMethod]
        public async Task SalirDelCanal_LoQuitaDelConteo()
        {
            var viewer = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            var other = await _app.JoinAsync(Demo.Channel1, Demo.Viewer2);

            await viewer.Connection.InvokeAsync("LeaveChannel", Demo.Channel1);

            Assert.AreEqual(1, (await other.GetViewersAsync(Demo.Channel1)).Count);
        }
    }
}
