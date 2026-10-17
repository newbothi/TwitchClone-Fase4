using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Integration.Media
{
    // Lo que MediaMTX le pregunta / avisa a la app (api/media/*), por HTTP real.
    //   auth                → ¿OBS puede publicar con esta clave? ¿se puede ver?
    //   hooks/available     → el stream empezó  (runOnAvailable)
    //   hooks/unavailable   → el stream terminó (runOnUnavailable)
    [TestClass]
    public class MediaHooksTests
    {
        private AppUnderTest _app = null!;

        [TestInitialize]
        public async Task Start() => _app = await AppUnderTest.StartAsync();

        [TestCleanup]
        public async Task Stop() => await _app.DisposeAsync();

        private static object Publish(string path, string? query = null, string? password = null, string? token = null) => new
        {
            user = "",
            password = password ?? "",
            token = token ?? "",
            ip = "192.168.1.50",
            action = "publish",
            path,
            protocol = "rtmp",
            id = (string?)null,
            query = query ?? ""
        };

        private static object Action(string action, string path = "live/1", string ip = "192.168.1.50") =>
            new { user = "", password = "", ip, action, path, protocol = "hls", query = "" };

        // ── Secreto compartido ───────────────────────────────────────────

        [TestMethod]
        [DataRow("", DisplayName = "Sin secreto")]
        [DataRow("otro-secreto", DisplayName = "Secreto incorrecto")]
        [DataRow("secreto-de-pruebaS", DisplayName = "Distinta mayúscula")]
        public async Task SinElSecretoCorrecto_TodoSeRechaza(string secret)
        {
            var auth = await _app.PostAuthAsync(Publish("live/1", $"key={Demo.Channel1Key}"), secret);
            var available = await _app.PostHookAsync("available", "live/1", secret);
            var unavailable = await _app.PostHookAsync("unavailable", "live/1", secret);

            Assert.AreEqual(HttpStatusCode.Unauthorized, auth.StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, available.StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, unavailable.StatusCode);
            Assert.IsFalse(_app.Db.Channels[Demo.Channel1].IsLive, "Nadie de afuera puede poner un canal EN VIVO.");
        }

        [TestMethod]
        public async Task SinSecretoConfigurado_SoloSeAceptaDesdeLaMismaMaquina()
        {
            await _app.DisposeAsync();
            _app = await AppUnderTest.StartAsync(hookSecret: "");

            // La prueba llama desde 127.0.0.1, como MediaMTX corriendo en el mismo PC.
            var response = await _app.PostHookAsync("available", "live/1", secret: "");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // ── Publicar desde OBS ───────────────────────────────────────────

        [TestMethod]
        [DataRow("key=" + Demo.Channel1Key, DisplayName = "Clave en la query (lo normal en OBS)")]
        [DataRow("?key=" + Demo.Channel1Key, DisplayName = "Query con '?' adelante")]
        [DataRow("otra=1&key=" + Demo.Channel1Key, DisplayName = "Con más parámetros")]
        public async Task Publicar_ConLaClaveCorrecta_SePermite(string query)
        {
            var response = await _app.PostAuthAsync(Publish("live/1", query));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [TestMethod]
        public async Task Publicar_ConLaClaveEnPasswordOToken_TambienSirve()
        {
            Assert.AreEqual(HttpStatusCode.OK, (await _app.PostAuthAsync(Publish("live/1", password: Demo.Channel1Key))).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await _app.PostAuthAsync(Publish("live/1", token: Demo.Channel1Key))).StatusCode);
        }

        [TestMethod]
        public async Task Publicar_SinClave_Es401()
        {
            var response = await _app.PostAuthAsync(Publish("live/1"));

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [TestMethod]
        [DataRow("live/1", "key=sk_incorrecta", DisplayName = "Clave incorrecta")]
        [DataRow("live/2", "key=" + Demo.Channel1Key, DisplayName = "Clave de otro canal")]
        [DataRow("live/3", "key=" + Demo.SuspendedChannelKey, DisplayName = "Dueño suspendido")]
        [DataRow("live/999", "key=" + Demo.Channel1Key, DisplayName = "Canal inexistente")]
        [DataRow("live/abc", "key=" + Demo.Channel1Key, DisplayName = "Path inválido")]
        [DataRow("live/1/extra", "key=" + Demo.Channel1Key, DisplayName = "Path con subcarpeta")]
        public async Task Publicar_Prohibido_Es403(string path, string query)
        {
            var response = await _app.PostAuthAsync(Publish(path, query));

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // ── Ver y administrar ────────────────────────────────────────────

        [TestMethod]
        public async Task Ver_EsPublicoParaRutasValidas()
        {
            Assert.AreEqual(HttpStatusCode.OK, (await _app.PostAuthAsync(Action("read", "live/1"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await _app.PostAuthAsync(Action("read", "otra/cosa"))).StatusCode);
        }

        [TestMethod]
        [DataRow("playback")]
        [DataRow("api")]
        [DataRow("metrics")]
        [DataRow("pprof")]
        public async Task Administrar_SoloDesdeLaMismaMaquina(string action)
        {
            Assert.AreEqual(HttpStatusCode.OK, (await _app.PostAuthAsync(Action(action, ip: "127.0.0.1"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await _app.PostAuthAsync(Action(action, ip: "::1"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await _app.PostAuthAsync(Action(action, ip: "203.0.113.7"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await _app.PostAuthAsync(Action(action, ip: "no-es-ip"))).StatusCode);
        }

        [TestMethod]
        public async Task AccionDesconocida_SeRechaza()
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, (await _app.PostAuthAsync(Action("borrar-todo"))).StatusCode);
        }

        // ── Empieza / termina el stream ──────────────────────────────────

        [TestMethod]
        public async Task StreamEmpieza_ElCanalQuedaEnVivoYLosQueMiranSeEnteran()
        {
            var viewer = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            var otherChannel = await _app.JoinAsync(Demo.Channel2, Demo.Viewer2);

            var response = await _app.PostHookAsync("available", "live/1");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.IsTrue(body.GetProperty("changed").GetBoolean());
            Assert.IsTrue(body.GetProperty("live").GetBoolean());

            Assert.IsTrue(_app.Db.Channels[Demo.Channel1].IsLive);
            await viewer.WaitForAsync("StreamStarted");
            Assert.AreEqual(1, _app.Db.Sessions.Count);
            Assert.AreEqual(1, _app.Db.LiveNotifications.Count, "Se avisa a los seguidores.");

            // Barrera: ahora empieza el canal 2. Cuando a ese espectador le llega SU StreamStarted,
            // el del canal 1 (enviado antes) ya le habría llegado si se hubiera filtrado.
            await _app.PostHookAsync("available", "live/2");
            await otherChannel.WaitForAsync("StreamStarted");
            Assert.AreEqual(1, otherChannel.Received("StreamStarted").Count, "El aviso del canal 1 no le llega al canal 2.");
        }

        [TestMethod]
        public async Task WebhookRepetido_NoDuplicaNada()
        {
            var viewer = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);

            await _app.PostHookAsync("available", "live/1");
            var second = await _app.PostHookAsync("available", "live/1");

            var body = await second.Content.ReadFromJsonAsync<JsonElement>();
            Assert.IsFalse(body.GetProperty("changed").GetBoolean());

            await _app.PostHookAsync("unavailable", "live/1");
            await viewer.WaitForAsync("StreamEnded");
            Assert.AreEqual(1, viewer.Received("StreamStarted").Count);
            Assert.AreEqual(1, _app.Db.Sessions.Count);
            Assert.AreEqual(1, _app.Db.LiveNotifications.Count);
        }

        [TestMethod]
        public async Task StreamTermina_ElCanalQuedaOfflineYSeCierraLaSesion()
        {
            var viewer = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            await _app.PostHookAsync("available", "live/1");
            _app.Db.Clock.Advance(TimeSpan.FromMinutes(45));

            var response = await _app.PostHookAsync("unavailable", "live/1");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsFalse(_app.Db.Channels[Demo.Channel1].IsLive);
            await viewer.WaitForAsync("StreamEnded");
            Assert.AreEqual(45 * 60, _app.Db.Sessions.Single().DurationSeconds);
        }

        [TestMethod]
        [DataRow("available")]
        [DataRow("unavailable")]
        public async Task Hooks_RutaInvalida400_CanalInexistente404(string hook)
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, (await _app.PostHookAsync(hook, "live/abc")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await _app.PostHookAsync(hook, "")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await _app.PostHookAsync(hook, "live/999")).StatusCode);
        }
    }
}
