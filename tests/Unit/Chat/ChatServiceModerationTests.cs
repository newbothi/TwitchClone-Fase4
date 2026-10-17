namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Chat
{
    // Moderación del chat: quién puede hacer qué y qué efecto tiene.
    [TestClass]
    public class ChatServiceModerationTests
    {
        private const string NoPermission = "No tienes permisos de moderación en este canal.";

        private ChatTestContext _ctx = null!;

        [TestInitialize]
        public void Setup() => _ctx = new ChatTestContext();

        // ── Permisos ─────────────────────────────────────────────────────

        [TestMethod]
        public async Task PuedenModerar_ElDuenoDelCanalYLosAdmin()
        {
            Assert.IsTrue(await _ctx.Chat.CanModerateAsync(Demo.Channel1, As.Owner));
            Assert.IsTrue(await _ctx.Chat.CanModerateAsync(Demo.Channel1, As.Admin));
            Assert.IsTrue(await _ctx.Chat.CanModerateAsync(Demo.Channel2, As.Admin), "Un Admin modera cualquier canal.");
        }

        [TestMethod]
        public async Task NoPuedenModerar_ViewersNiDuenosDeOtrosCanales()
        {
            Assert.IsFalse(await _ctx.Chat.CanModerateAsync(Demo.Channel1, As.Viewer1));
            Assert.IsFalse(await _ctx.Chat.CanModerateAsync(Demo.Channel1, As.OtherOwner));
            Assert.IsFalse(await _ctx.Chat.CanModerateAsync(Demo.MissingChannel, As.Viewer1));
        }

        [TestMethod]
        public async Task UnViewer_NoPuedeUsarNingunaAccionDeModeracion()
        {
            var message = (await _ctx.Send(As.Viewer2, "hola")).Message!;

            Assert.AreEqual(NoPermission, (await _ctx.Chat.DeleteMessageAsync(Demo.Channel1, message.Id, As.Viewer1)).Error);
            Assert.AreEqual(NoPermission, (await _ctx.Chat.TimeoutAsync(Demo.Channel1, Demo.Viewer2, 600, null, As.Viewer1)).Error);
            Assert.AreEqual(NoPermission, (await _ctx.Chat.BanAsync(Demo.Channel1, Demo.Viewer2, "spam", As.Viewer1)).Error);
            Assert.AreEqual(NoPermission, (await _ctx.Chat.UnbanAsync(Demo.Channel1, Demo.Viewer2, As.Viewer1)).Error);
            Assert.AreEqual(NoPermission, (await _ctx.Chat.ClearAsync(Demo.Channel1, As.Viewer1)).Error);

            Assert.AreEqual(0, _ctx.Db.Bans.Count);
            Assert.AreEqual(1, _ctx.Db.MessagesOf(Demo.Channel1).Count);
        }

        // ── Borrar un mensaje ────────────────────────────────────────────

        [TestMethod]
        public async Task BorrarMensaje_LoMarcaComoBorradoYDevuelveElAutor()
        {
            var message = (await _ctx.Send(As.Viewer1, "spam spam")).Message!;

            var result = await _ctx.Chat.DeleteMessageAsync(Demo.Channel1, message.Id, As.Owner);

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual(Demo.Viewer1Name, result.TargetDisplayName);
            Assert.AreEqual(0, _ctx.Db.MessagesOf(Demo.Channel1).Count);
            Assert.AreEqual(0, (await _ctx.Chat.GetHistoryAsync(Demo.Channel1)).Count, "Tampoco aparece en el historial.");
        }

        [TestMethod]
        public async Task BorrarMensaje_DeOtroCanal_NoSePuede()
        {
            var otherChannelMessage = (await _ctx.Send(As.Viewer1, "hola", Demo.Channel2)).Message!;

            var result = await _ctx.Chat.DeleteMessageAsync(Demo.Channel1, otherChannelMessage.Id, As.Owner);

            Assert.IsFalse(result.Ok, "El dueño del canal 1 no puede borrar mensajes del canal 2 pasando su id.");
            Assert.AreEqual(1, _ctx.Db.MessagesOf(Demo.Channel2).Count);
        }

        [TestMethod]
        [DataRow("no-es-un-id")]
        [DataRow("000000000000000000000000")]
        public async Task BorrarMensaje_IdInvalidoOInexistente_FallaSinRomperse(string messageId)
        {
            var result = await _ctx.Chat.DeleteMessageAsync(Demo.Channel1, messageId, As.Owner);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("Mensaje no encontrado.", result.Error);
        }

        // ── Timeout ──────────────────────────────────────────────────────

        [TestMethod]
        public async Task Timeout_SilenciaAlUsuarioYBorraSusMensajes()
        {
            await _ctx.Send(As.Viewer1, "mensaje 1");
            await _ctx.Send(As.Viewer1, "mensaje 2");
            await _ctx.Send(As.Viewer2, "yo me porto bien");

            var result = await _ctx.Chat.TimeoutAsync(Demo.Channel1, Demo.Viewer1, 600, null, As.Owner);

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual(Demo.Viewer1Name, result.TargetDisplayName);

            var remaining = _ctx.Db.MessagesOf(Demo.Channel1);
            Assert.AreEqual(1, remaining.Count);
            Assert.AreEqual(Demo.Viewer2, remaining[0].UserId);

            var ban = _ctx.Db.Bans[(Demo.Channel1, Demo.Viewer1)];
            Assert.AreEqual(_ctx.Clock.GetUtcNow().UtcDateTime.AddSeconds(600), ban.ExpiresAt);
        }

        [TestMethod]
        public async Task Timeout_TieneEfectoInmediato_AunqueElUsuarioHayaEscritoRecien()
        {
            // El primer mensaje deja en caché "no está baneado" por 15 s.
            Assert.IsTrue((await _ctx.Send(As.Viewer1, "hola")).Ok);

            await _ctx.Chat.TimeoutAsync(Demo.Channel1, Demo.Viewer1, 300, null, As.Owner);
            var result = await _ctx.Send(As.Viewer1, "sigo escribiendo?");

            Assert.AreEqual("timed_out", result.ErrorCode, "La sanción invalida la caché al instante.");
            Assert.AreEqual(300, result.RetryAfterSeconds);
        }

        [TestMethod]
        [DataRow(0, 1)]
        [DataRow(-50, 1)]
        [DataRow(99_999_999, 14 * 24 * 60 * 60)]
        public async Task Timeout_LaDuracionSeAcotaEntre1SegundoY14Dias(int requested, int expectedSeconds)
        {
            await _ctx.Chat.TimeoutAsync(Demo.Channel1, Demo.Viewer1, requested, null, As.Owner);

            var ban = _ctx.Db.Bans[(Demo.Channel1, Demo.Viewer1)];
            Assert.AreEqual(_ctx.Clock.GetUtcNow().UtcDateTime.AddSeconds(expectedSeconds), ban.ExpiresAt);
        }

        // ── Ban ──────────────────────────────────────────────────────────

        [TestMethod]
        public async Task Ban_EsPermanenteYBloqueaElChat()
        {
            await _ctx.Send(As.Viewer1, "hola");

            var result = await _ctx.Chat.BanAsync(Demo.Channel1, Demo.Viewer1, "spam", As.Owner);

            Assert.IsTrue(result.Ok, result.Error);
            Assert.IsNull(_ctx.Db.Bans[(Demo.Channel1, Demo.Viewer1)].ExpiresAt);
            Assert.AreEqual("spam", _ctx.Db.Bans[(Demo.Channel1, Demo.Viewer1)].Reason);
            Assert.AreEqual("banned", (await _ctx.Send(As.Viewer1, "hola?")).ErrorCode);
            Assert.AreEqual(0, _ctx.Db.MessagesOf(Demo.Channel1).Count, "Se borran sus mensajes.");
        }

        [TestMethod]
        [DataRow(null, "Sin motivo")]
        [DataRow("   ", "Sin motivo")]
        [DataRow("  insultos  ", "insultos")]
        public async Task Ban_ElMotivoSeNormaliza(string? reason, string expected)
        {
            await _ctx.Chat.BanAsync(Demo.Channel1, Demo.Viewer1, reason, As.Owner);

            Assert.AreEqual(expected, _ctx.Db.Bans[(Demo.Channel1, Demo.Viewer1)].Reason);
        }

        [TestMethod]
        public async Task Ban_UnMotivoMuyLargoSeRecortaA500()
        {
            await _ctx.Chat.BanAsync(Demo.Channel1, Demo.Viewer1, new string('x', 2000), As.Owner);

            Assert.AreEqual(500, _ctx.Db.Bans[(Demo.Channel1, Demo.Viewer1)].Reason.Length);
        }

        [TestMethod]
        public async Task NadiePuedeSancionarAlDuenoDelCanal_NiSiquieraUnAdmin()
        {
            var result = await _ctx.Chat.BanAsync(Demo.Channel1, Demo.Owner1, "golpe de estado", As.Admin);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("No puedes sancionar al dueño del canal.", result.Error);
        }

        [TestMethod]
        public async Task NadiePuedeSancionarseASiMismo()
        {
            var result = await _ctx.Chat.TimeoutAsync(Demo.Channel2, Demo.Admin, 60, null, As.Admin);

            Assert.AreEqual("No puedes aplicarte una sanción a ti mismo.", result.Error);
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("usuario-que-no-existe")]
        public async Task SancionarAUnUsuarioInexistente_Falla(string targetUserId)
        {
            var result = await _ctx.Chat.BanAsync(Demo.Channel1, targetUserId, "x", As.Owner);

            Assert.AreEqual("Usuario no encontrado.", result.Error);
            Assert.AreEqual(0, _ctx.Db.Bans.Count);
        }

        [TestMethod]
        public async Task UnAdmin_PuedeSancionarEnCualquierCanal()
        {
            var result = await _ctx.Chat.TimeoutAsync(Demo.Channel2, Demo.Viewer1, 60, null, As.Admin);

            Assert.IsTrue(result.Ok, result.Error);
        }

        // ── Quitar sanción ───────────────────────────────────────────────

        [TestMethod]
        public async Task Unban_PermiteVolverAEscribirAlInstante()
        {
            await _ctx.Chat.BanAsync(Demo.Channel1, Demo.Viewer1, "spam", As.Owner);
            Assert.AreEqual("banned", (await _ctx.Send(As.Viewer1, "hola?")).ErrorCode);

            var result = await _ctx.Chat.UnbanAsync(Demo.Channel1, Demo.Viewer1, As.Owner);

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual(Demo.Viewer1Name, result.TargetDisplayName);
            Assert.IsTrue((await _ctx.Send(As.Viewer1, "gracias")).Ok);
        }

        [TestMethod]
        public async Task Unban_DeAlguienSinSancion_AvisaQueNoEstabaBaneado()
        {
            var result = await _ctx.Chat.UnbanAsync(Demo.Channel1, Demo.Viewer1, As.Owner);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("Ese usuario no estaba baneado ni silenciado.", result.Error);
        }

        // ── Limpiar el chat ──────────────────────────────────────────────

        [TestMethod]
        public async Task LimpiarChat_BorraSoloLosMensajesDeEseCanal()
        {
            await _ctx.Send(As.Viewer1, "a");
            await _ctx.Send(As.Viewer2, "b");
            await _ctx.Send(As.Viewer1, "otro canal", Demo.Channel2);

            var result = await _ctx.Chat.ClearAsync(Demo.Channel1, As.Owner);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(0, _ctx.Db.MessagesOf(Demo.Channel1).Count);
            Assert.AreEqual(1, _ctx.Db.MessagesOf(Demo.Channel2).Count);
        }
    }
}
