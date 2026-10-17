using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Streaming
{
    // Rutas de MediaMTX: lo que llega en los webhooks y las URL que se muestran al usuario.
    [TestClass]
    public class MediaServerSettingsTests
    {
        private readonly MediaServerSettings _settings = new();

        [TestMethod]
        [DataRow("live/1", 1)]
        [DataRow("live/42", 42)]
        [DataRow("/live/42/", 42)]
        [DataRow("live/999999999", 999999999)]
        public void RutaValida_DevuelveElCanal(string path, int expected)
        {
            Assert.IsTrue(_settings.TryParseChannelId(path, out var channelId));
            Assert.AreEqual(expected, channelId);
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("live")]
        [DataRow("live/")]
        [DataRow("live/0", DisplayName = "Canal 0")]
        [DataRow("live/-3", DisplayName = "Negativo")]
        [DataRow("live/abc", DisplayName = "Letras")]
        [DataRow("live/12/extra", DisplayName = "Subcarpeta")]
        [DataRow("live/1234567890", DisplayName = "Más de 9 dígitos")]
        [DataRow("live/１２", DisplayName = "Dígitos no ASCII")]
        [DataRow("LIVE/12", DisplayName = "Prefijo en mayúsculas")]
        [DataRow("otra/12", DisplayName = "Otro prefijo")]
        [DataRow("live/12?key=sk_x", DisplayName = "Con la clave pegada")]
        public void RutaInvalida_SeRechaza(string? path)
        {
            Assert.IsFalse(_settings.TryParseChannelId(path, out var channelId));
            Assert.AreEqual(0, channelId);
        }

        [TestMethod]
        public void PrefijoPersonalizado_SeRespeta()
        {
            var custom = new MediaServerSettings { PathPrefix = "/tv/" };

            Assert.IsTrue(custom.TryParseChannelId("tv/7", out var channelId));
            Assert.AreEqual(7, channelId);
            Assert.IsFalse(custom.TryParseChannelId("live/7", out _));
        }

        [TestMethod]
        public void UrlsQueSeMuestran()
        {
            var settings = new MediaServerSettings { HlsBaseUrl = "http://localhost:8888/" };

            Assert.AreEqual("live/12", settings.PathFor(12));
            Assert.AreEqual("http://localhost:8888/live/12/index.m3u8", settings.HlsUrlFor(12), "Sin doble barra.");
            Assert.AreEqual("12?key=sk_abc", MediaServerSettings.ObsStreamKeyFor(12, "sk_abc"));
        }

        [TestMethod]
        public void LaUrlPublicaDelVideo_NoContieneLaClave()
        {
            var url = _settings.HlsUrlFor(1);

            Assert.IsFalse(url.Contains("key", StringComparison.OrdinalIgnoreCase), url);
            Assert.IsFalse(url.Contains(Demo.Channel1Key, StringComparison.Ordinal), url);
        }
    }
}
