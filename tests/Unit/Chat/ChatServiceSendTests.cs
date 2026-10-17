using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Chat
{
    // El pipeline de envío del chat (ChatService.SendAsync), regla por regla.
    [TestClass]
    public class ChatServiceSendTests
    {
        private ChatTestContext _ctx = null!;

        [TestInitialize]
        public void Setup() => _ctx = new ChatTestContext();

        // ── Mensaje válido ───────────────────────────────────────────────

        [TestMethod]
        public async Task MensajeValido_SeGuardaYDevuelveElNombreDelServidor()
        {
            var result = await _ctx.Send(As.Viewer1, "hola chat");

            Assert.IsTrue(result.Ok, result.ErrorMessage);
            var dto = result.Message!;
            Assert.AreEqual(Demo.Viewer1, dto.UserId);
            Assert.AreEqual(Demo.Viewer1Name, dto.DisplayName, "El nombre sale de la base, nunca del navegador.");
            Assert.AreEqual(Demo.Channel1, dto.ChannelId);
            Assert.AreEqual(ChatColors.ForUser(Demo.Viewer1), dto.Color);
            Assert.AreEqual(_ctx.Clock.GetUtcNow().UtcDateTime, dto.SentAt);
            Assert.AreEqual(DateTimeKind.Utc, dto.SentAt.Kind);

            var stored = _ctx.Db.MessagesOf(Demo.Channel1);
            Assert.AreEqual(1, stored.Count);
            Assert.AreEqual("hola chat", stored[0].Content);
            Assert.AreEqual(dto.Id, stored[0].Id);
        }

        [TestMethod]
        public async Task HtmlEnElMensaje_ViajaComoTextoPlano()
        {
            const string attack = "<img src=x onerror=alert(1)><script>robar()</script>";

            var result = await _ctx.Send(As.Viewer1, attack);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(1, result.Message!.Fragments.Count);
            Assert.AreEqual(ChatFragment.TextType, result.Message.Fragments[0].Type);
            Assert.AreEqual(attack, result.Message.Fragments[0].Text, "El navegador lo pinta con textContent: se ve como texto, no se ejecuta.");
        }

        [TestMethod]
        public async Task SaltosDeLineaYCaracteresDeControl_SeNormalizan()
        {
            var result = await _ctx.Send(As.Viewer1, "  hola\r\nmundo\t!\u0007  ");

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("hola  mundo !", _ctx.Db.MessagesOf(Demo.Channel1)[0].Content);
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("     ")]
        [DataRow("\r\n\t")]
        public async Task MensajeVacio_SeRechaza(string text)
        {
            var result = await _ctx.Send(As.Viewer1, text);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("empty", result.ErrorCode);
        }

        [TestMethod]
        public async Task Mensaje_DeHasta500Caracteres_PasaYDe501SeRechaza()
        {
            var limit = await _ctx.Send(As.Viewer1, new string('a', ChatService.MaxMessageLength));
            var tooLong = await _ctx.Send(As.Viewer2, new string('a', ChatService.MaxMessageLength + 1));

            Assert.IsTrue(limit.Ok);
            Assert.IsFalse(tooLong.Ok);
            Assert.AreEqual("too_long", tooLong.ErrorCode);
        }

        // ── Quién puede escribir ─────────────────────────────────────────

        [TestMethod]
        public async Task CanalInexistente_SeRechaza()
        {
            var result = await _ctx.Send(As.Viewer1, "hola", Demo.MissingChannel);

            Assert.AreEqual("channel_not_found", result.ErrorCode);
        }

        [TestMethod]
        public async Task UsuarioInexistente_SeRechaza()
        {
            var result = await _ctx.Send(As.Ghost, "hola");

            Assert.AreEqual("user_not_found", result.ErrorCode);
        }

        [TestMethod]
        public async Task CuentaSuspendida_NoPuedeEscribir()
        {
            var result = await _ctx.Send(As.Suspended, "hola");

            Assert.AreEqual("suspended", result.ErrorCode);
            Assert.AreEqual(0, _ctx.Db.MessagesOf(Demo.Channel1).Count);
        }

        [TestMethod]
        public async Task UsuarioBaneado_NoPuedeEscribir()
        {
            _ctx.Db.Bans[(Demo.Channel1, Demo.Viewer1)] = new BannedUser { ChannelId = Demo.Channel1, UserId = Demo.Viewer1, ExpiresAt = null };

            var result = await _ctx.Send(As.Viewer1, "hola");

            Assert.AreEqual("banned", result.ErrorCode);
        }

        [TestMethod]
        public async Task ElBanDeUnCanal_NoAfectaAOtroCanal()
        {
            _ctx.Db.Bans[(Demo.Channel1, Demo.Viewer1)] = new BannedUser { ChannelId = Demo.Channel1, UserId = Demo.Viewer1 };

            var result = await _ctx.Send(As.Viewer1, "hola", Demo.Channel2);

            Assert.IsTrue(result.Ok);
        }

        [TestMethod]
        public async Task DuenoDelCanal_NuncaQuedaBloqueadoPorUnBan()
        {
            _ctx.Db.Bans[(Demo.Channel1, Demo.Owner1)] = new BannedUser { ChannelId = Demo.Channel1, UserId = Demo.Owner1 };

            var result = await _ctx.Send(As.Owner, "sigo aquí");

            Assert.IsTrue(result.Ok);
        }

        [TestMethod]
        public async Task Timeout_IndicaCuantoFaltaYAlVencerPuedeVolverAEscribir()
        {
            var now = _ctx.Clock.GetUtcNow().UtcDateTime;
            _ctx.Db.Bans[(Demo.Channel1, Demo.Viewer1)] = new BannedUser { ChannelId = Demo.Channel1, UserId = Demo.Viewer1, ExpiresAt = now.AddSeconds(120) };

            var first = await _ctx.Send(As.Viewer1, "hola");
            Assert.AreEqual("timed_out", first.ErrorCode);
            Assert.AreEqual(120, first.RetryAfterSeconds);

            _ctx.Clock.AdvanceSeconds(60);
            var middle = await _ctx.Send(As.Viewer1, "hola");
            Assert.AreEqual("timed_out", middle.ErrorCode);
            Assert.AreEqual(60, middle.RetryAfterSeconds);

            _ctx.Clock.AdvanceSeconds(60);
            var after = await _ctx.Send(As.Viewer1, "ya puedo");
            Assert.IsTrue(after.Ok, after.ErrorMessage);
        }

        // ── Solo seguidores ──────────────────────────────────────────────

        [TestMethod]
        public async Task SoloSeguidores_QuienNoSigueNoPuedeEscribir()
        {
            _ctx.ConfigureChannel1(followersOnly: true);

            var result = await _ctx.Send(As.Viewer1, "hola");

            Assert.AreEqual("followers_only", result.ErrorCode);
        }

        [TestMethod]
        public async Task SoloSeguidores_QuienSigueSiPuede()
        {
            _ctx.ConfigureChannel1(followersOnly: true);

            var result = await _ctx.Send(As.Viewer2, "hola"); // ViewerDos sigue al canal 1

            Assert.IsTrue(result.Ok, result.ErrorMessage);
        }

        [TestMethod]
        public async Task SoloSeguidores_DuenoYAdminEstanExentos()
        {
            _ctx.ConfigureChannel1(followersOnly: true);

            Assert.IsTrue((await _ctx.Send(As.Owner, "hola")).Ok);
            Assert.IsTrue((await _ctx.Send(As.Admin, "hola")).Ok);
        }

        // ── Palabras bloqueadas ──────────────────────────────────────────

        [TestMethod]
        [DataRow("eres tonto")]
        [DataRow("ERES TONTO")]
        [DataRow("tontoooo jaja")]
        public async Task PalabraBloqueada_SeDetectaSinImportarMayusculas(string text)
        {
            _ctx.ConfigureChannel1(bannedWords: "tonto");

            var result = await _ctx.Send(As.Viewer1, text);

            Assert.AreEqual("blocked_term", result.ErrorCode);
            Assert.AreEqual(0, _ctx.Db.MessagesOf(Demo.Channel1).Count);
        }

        [TestMethod]
        public async Task PalabraBloqueada_NoAplicaAlDuenoNiAlAdmin()
        {
            _ctx.ConfigureChannel1(bannedWords: "tonto");

            Assert.IsTrue((await _ctx.Send(As.Owner, "no seas tonto")).Ok);
            Assert.IsTrue((await _ctx.Send(As.Admin, "no seas tonto")).Ok);
        }

        // ── Modo lento ───────────────────────────────────────────────────

        [TestMethod]
        public async Task ModoLento_RechazaHastaQuePaseElTiempoYDiceCuantoFalta()
        {
            _ctx.ConfigureChannel1(slowModeSeconds: 10);

            Assert.IsTrue((await _ctx.Send(As.Viewer1, "uno")).Ok);

            var tooSoon = await _ctx.Send(As.Viewer1, "dos");
            Assert.AreEqual("slow_mode", tooSoon.ErrorCode);
            Assert.AreEqual(10, tooSoon.RetryAfterSeconds);

            _ctx.Clock.AdvanceSeconds(4);
            var stillWaiting = await _ctx.Send(As.Viewer1, "dos");
            Assert.AreEqual(6, stillWaiting.RetryAfterSeconds);

            _ctx.Clock.AdvanceSeconds(6);
            Assert.IsTrue((await _ctx.Send(As.Viewer1, "dos")).Ok);
        }

        [TestMethod]
        public async Task ModoLento_UnMensajeRechazadoNoReiniciaLaEspera()
        {
            _ctx.ConfigureChannel1(slowModeSeconds: 10);
            await _ctx.Send(As.Viewer1, "uno");

            _ctx.Clock.AdvanceSeconds(5);
            await _ctx.Send(As.Viewer1, "insistiendo"); // rechazado
            _ctx.Clock.AdvanceSeconds(5);

            Assert.IsTrue((await _ctx.Send(As.Viewer1, "dos")).Ok, "Cuenta desde el último mensaje ACEPTADO.");
        }

        [TestMethod]
        public async Task ModoLento_EsPorUsuario()
        {
            _ctx.ConfigureChannel1(slowModeSeconds: 30);

            Assert.IsTrue((await _ctx.Send(As.Viewer1, "hola")).Ok);
            Assert.IsTrue((await _ctx.Send(As.Viewer2, "hola")).Ok, "Otro usuario no espera por el primero.");
        }

        [TestMethod]
        public async Task ModoLento_DuenoYAdminEstanExentos()
        {
            _ctx.ConfigureChannel1(slowModeSeconds: 30);

            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue((await _ctx.Send(As.Owner, $"dueño {i}")).Ok);
                Assert.IsTrue((await _ctx.Send(As.Admin, $"admin {i}")).Ok);
            }
        }

        // ── Límite de mensajes (anti-spam) ───────────────────────────────

        [TestMethod]
        public async Task LimiteDeMensajes_ElMensaje21EnMenosDe30sSeRechaza()
        {
            for (var i = 0; i < 20; i++)
            {
                var ok = await _ctx.Send(As.Viewer1, $"mensaje {i}");
                Assert.IsTrue(ok.Ok, $"El mensaje {i} debía pasar: {ok.ErrorMessage}");
                _ctx.Clock.AdvanceSeconds(1);
            }

            var limited = await _ctx.Send(As.Viewer1, "uno más");

            Assert.AreEqual("rate_limited", limited.ErrorCode);
            Assert.AreEqual(10, limited.RetryAfterSeconds, "El primero de la ventana vence en 30 - 20 = 10 s.");
        }

        [TestMethod]
        public async Task LimiteDeMensajes_EsperarLoQueIndicaRetryAfter_AlcanzaParaVolverAEscribir()
        {
            for (var i = 0; i < 20; i++) await _ctx.Send(As.Viewer1, $"mensaje {i}");
            var limited = await _ctx.Send(As.Viewer1, "uno más");
            Assert.AreEqual("rate_limited", limited.ErrorCode);

            _ctx.Clock.AdvanceSeconds(limited.RetryAfterSeconds!.Value);

            var retry = await _ctx.Send(As.Viewer1, "ahora sí");
            Assert.IsTrue(retry.Ok, $"El servidor dijo 'espera {limited.RetryAfterSeconds} s' y después de esperar siguió rechazando: {retry.ErrorCode}");
        }

        [TestMethod]
        public async Task LimiteDeMensajes_DuenoTieneUnLimiteMasAlto()
        {
            for (var i = 0; i < 100; i++)
                Assert.IsTrue((await _ctx.Send(As.Owner, $"anuncio {i}")).Ok, $"El dueño puede mandar hasta 100 (falló el {i}).");

            Assert.AreEqual("rate_limited", (await _ctx.Send(As.Owner, "101")).ErrorCode);
        }

        [TestMethod]
        public async Task LimitesDeUnCanal_NoAfectanAOtroCanal()
        {
            _ctx.Db.FilterFor(Demo.Channel1).SlowModeEnabled = true;
            _ctx.Db.FilterFor(Demo.Channel1).SlowModeSeconds = 60;

            Assert.IsTrue((await _ctx.Send(As.Viewer1, "hola canal 1")).Ok);
            Assert.IsTrue((await _ctx.Send(As.Viewer1, "hola canal 2", Demo.Channel2)).Ok);
        }

        // ── Contenido del mensaje ────────────────────────────────────────

        [TestMethod]
        public async Task Emotes_SeConviertenEnFragmentosConSuImagen()
        {
            var result = await _ctx.Send(As.Viewer1, "hola pepega y :kappa:");

            var f = result.Message!.Fragments;
            Assert.AreEqual(4, f.Count);
            Assert.AreEqual("hola ", f[0].Text);
            Assert.AreEqual(ChatFragment.EmoteType, f[1].Type);
            Assert.AreEqual("/emotes/1_pepega.png", f[1].EmoteUrl);
            Assert.AreEqual(" y ", f[2].Text);
            Assert.AreEqual("/emotes/1_kappa.png", f[3].EmoteUrl);
        }

        [TestMethod]
        public async Task EmotesDeUnCanal_NoFuncionanEnOtroCanal()
        {
            var result = await _ctx.Send(As.Viewer1, "pepega", Demo.Channel2);

            Assert.AreEqual(1, result.Message!.Fragments.Count);
            Assert.AreEqual(ChatFragment.TextType, result.Message.Fragments[0].Type);
        }

        [TestMethod]
        public async Task Insignias_StreamerYAdmin()
        {
            var owner = await _ctx.Send(As.Owner, "hola");
            var admin = await _ctx.Send(As.Admin, "hola");
            var viewer = await _ctx.Send(As.Viewer1, "hola");

            CollectionAssert.AreEqual(new[] { "broadcaster" }, owner.Message!.Badges);
            CollectionAssert.AreEqual(new[] { "admin" }, admin.Message!.Badges);
            Assert.AreEqual(0, viewer.Message!.Badges.Count);
        }

        // ── Si MongoDB falla ─────────────────────────────────────────────

        [TestMethod]
        public async Task MongoCaidoAlGuardar_ElMensajeIgualSeDifunde()
        {
            _ctx.Db.FailChatSaves = true;

            var result = await _ctx.Send(As.Viewer1, "hola");

            Assert.IsTrue(result.Ok, "El chat en vivo no se cae por MongoDB.");
            Assert.IsNotNull(result.Message);
            Assert.AreEqual(0, _ctx.Db.MessagesOf(Demo.Channel1).Count, "Solo no queda en el historial.");
        }

        [TestMethod]
        public async Task MongoCaidoAlLeerLaConfiguracion_SeUsanValoresPorDefecto()
        {
            _ctx.Db.FailChatFilterReads = true;

            var result = await _ctx.Send(As.Viewer1, "hola");

            Assert.IsTrue(result.Ok, result.ErrorMessage);
        }
    }
}
