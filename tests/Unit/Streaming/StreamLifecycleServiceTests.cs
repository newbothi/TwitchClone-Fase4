using Microsoft.Extensions.Logging.Abstractions;
using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Streaming
{
    // Quién puede transmitir (MediaMTX pregunta) y qué pasa al empezar / terminar un directo.
    [TestClass]
    public class StreamLifecycleServiceTests
    {
        private InMemoryPlatform _db = null!;
        private StreamLifecycleService _service = null!;

        [TestInitialize]
        public void Setup()
        {
            _db = InMemoryPlatform.WithDemoData();
            _service = new StreamLifecycleService(
                new InMemoryChannelRepository(_db),
                new InMemoryFollowService(_db),
                new InMemoryNotificationService(_db),
                new InMemoryLiveMetricService(_db),
                new InMemoryStreamSessionService(_db),
                NullLogger<StreamLifecycleService>.Instance);
        }

        // ── Autorizar la publicación desde OBS ───────────────────────────

        [TestMethod]
        public async Task ClaveCorrecta_PuedeTransmitir()
        {
            Assert.AreEqual(PublishAuthorization.Allowed, await _service.AuthorizePublishAsync(Demo.Channel1, Demo.Channel1Key));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        public async Task SinClave_SeRechaza(string? key)
        {
            Assert.AreEqual(PublishAuthorization.MissingKey, await _service.AuthorizePublishAsync(Demo.Channel1, key));
        }

        [TestMethod]
        [DataRow("sk_ClaveDelCanalUno12", DisplayName = "Le falta un carácter")]
        [DataRow("sk_ClaveDelCanalUno1234", DisplayName = "Le sobra un carácter")]
        [DataRow("sk_claveDelCanalUno123", DisplayName = "Distinta mayúscula")]
        [DataRow(Demo.Channel2Key, DisplayName = "Clave de OTRO canal")]
        [DataRow(" sk_ClaveDelCanalUno123", DisplayName = "Con espacio")]
        public async Task ClaveIncorrecta_SeRechaza(string key)
        {
            Assert.AreEqual(PublishAuthorization.InvalidKey, await _service.AuthorizePublishAsync(Demo.Channel1, key));
        }

        [TestMethod]
        public async Task CanalSinClaveGuardada_NadiePuedeTransmitir_NiConClaveVacia()
        {
            _db.Channels[Demo.Channel1].StreamKey = "";

            Assert.AreEqual(PublishAuthorization.InvalidKey, await _service.AuthorizePublishAsync(Demo.Channel1, "cualquiera"));
        }

        [TestMethod]
        public async Task CanalInexistente_SeRechaza()
        {
            Assert.AreEqual(PublishAuthorization.ChannelNotFound, await _service.AuthorizePublishAsync(Demo.MissingChannel, Demo.Channel1Key));
        }

        [TestMethod]
        public async Task DuenoSuspendido_NoPuedeTransmitir_AunqueLaClaveSeaCorrecta()
        {
            Assert.AreEqual(PublishAuthorization.OwnerSuspended, await _service.AuthorizePublishAsync(Demo.SuspendedChannel, Demo.SuspendedChannelKey));
        }

        [TestMethod]
        public async Task DuenoSuspendido_ConClaveIncorrecta_DiceClaveIncorrecta()
        {
            // No se revela si el canal existe con un dueño suspendido a quien no tiene la clave.
            Assert.AreEqual(PublishAuthorization.InvalidKey, await _service.AuthorizePublishAsync(Demo.SuspendedChannel, "sk_otra"));
        }

        // ── Empieza el directo ───────────────────────────────────────────

        [TestMethod]
        public async Task EmpezarDirecto_MarcaEnVivoAbreSesionYAvisaASeguidores()
        {
            var change = await _service.MarkLiveAsync(Demo.Channel1);

            Assert.IsNotNull(change);
            Assert.IsTrue(change.Changed);
            Assert.AreEqual("Canal Uno", change.ChannelName);
            Assert.AreEqual(Demo.Owner1Name, change.OwnerDisplayName);
            Assert.IsTrue(_db.Channels[Demo.Channel1].IsLive);

            Assert.IsTrue(_db.Metrics["1"].IsLive);
            var session = _db.Sessions.Single();
            Assert.AreEqual("1", session.ChannelId);
            Assert.AreEqual("Charlando", session.CategoryName);
            Assert.IsNull(session.EndedAt);

            Assert.IsTrue(_db.LiveNotifications.TryDequeue(out var notification));
            CollectionAssert.AreEqual(new[] { Demo.Viewer2 }, notification.FollowerIds.ToList(), "Solo ViewerDos sigue al canal 1.");
            Assert.AreEqual(Demo.Owner1Name, notification.ChannelName);
        }

        [TestMethod]
        public async Task EmpezarDirecto_DosVeces_NoDuplicaSesionNiAvisos()
        {
            await _service.MarkLiveAsync(Demo.Channel1);
            var second = await _service.MarkLiveAsync(Demo.Channel1); // webhook repetido de MediaMTX

            Assert.IsNotNull(second);
            Assert.IsFalse(second.Changed);
            Assert.AreEqual(1, _db.Sessions.Count);
            Assert.AreEqual(1, _db.LiveNotifications.Count);
        }

        [TestMethod]
        public async Task EmpezarDirecto_ConMongoCaido_ElCanalIgualQuedaEnVivo()
        {
            _db.FailLiveMetrics = true;

            var change = await _service.MarkLiveAsync(Demo.Channel1);

            Assert.IsTrue(change!.Changed);
            Assert.IsTrue(_db.Channels[Demo.Channel1].IsLive);
            Assert.AreEqual(1, _db.LiveNotifications.Count, "Los avisos a seguidores no dependen de las métricas.");
        }

        [TestMethod]
        public async Task CanalInexistente_NoHaceNada()
        {
            Assert.IsNull(await _service.MarkLiveAsync(Demo.MissingChannel));
            Assert.IsNull(await _service.MarkOfflineAsync(Demo.MissingChannel));
            Assert.AreEqual(0, _db.Sessions.Count);
        }

        // ── Termina el directo ───────────────────────────────────────────

        [TestMethod]
        public async Task TerminarDirecto_CierraLaSesionConElPicoDeEspectadores()
        {
            await _service.MarkLiveAsync(Demo.Channel1);
            var metrics = new InMemoryLiveMetricService(_db);
            await metrics.SetViewerCountAsync("1", 12);
            await metrics.SetViewerCountAsync("1", 40);
            await metrics.SetViewerCountAsync("1", 25);
            _db.Clock.Advance(TimeSpan.FromMinutes(90));

            var change = await _service.MarkOfflineAsync(Demo.Channel1);

            Assert.IsTrue(change!.Changed);
            Assert.IsFalse(_db.Channels[Demo.Channel1].IsLive);
            Assert.IsFalse(_db.Metrics["1"].IsLive);

            var session = _db.Sessions.Single();
            Assert.IsNotNull(session.EndedAt);
            Assert.AreEqual(40, session.PeakViewers);
            Assert.AreEqual(90 * 60, session.DurationSeconds);
        }

        [TestMethod]
        public async Task TerminarDirecto_SinEstarEnVivo_NoCambiaNada()
        {
            var change = await _service.MarkOfflineAsync(Demo.Channel1);

            Assert.IsNotNull(change);
            Assert.IsFalse(change.Changed);
        }

        [TestMethod]
        public async Task TerminarDirecto_ConMongoCaido_ElCanalIgualQuedaOffline()
        {
            await _service.MarkLiveAsync(Demo.Channel1);
            _db.FailLiveMetrics = true;

            var change = await _service.MarkOfflineAsync(Demo.Channel1);

            Assert.IsTrue(change!.Changed);
            Assert.IsFalse(_db.Channels[Demo.Channel1].IsLive);
        }
    }
}
