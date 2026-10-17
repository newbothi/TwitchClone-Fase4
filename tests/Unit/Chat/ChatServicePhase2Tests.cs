using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Chat
{
    // Lo principal de la Fase 2: moderadores y VIP, enlaces, solo emotes, repetidos,
    // AutoMod, comandos, respuestas y mensaje fijado.
    [TestClass]
    public class ChatServicePhase2Tests
    {
        private ChatTestContext _ctx = null!;

        [TestInitialize]
        public void Setup() => _ctx = new ChatTestContext();

        // ── Moderadores y VIP ────────────────────────────────────────────

        [TestMethod]
        public async Task UnModerador_PuedeModerarSuCanal()
        {
            _ctx.MakeModerator(Demo.Viewer3);
            var message = (await _ctx.Send(As.Viewer1, "spam")).Message!;

            Assert.IsTrue(await _ctx.Chat.CanModerateAsync(Demo.Channel1, As.Viewer3));
            Assert.IsTrue((await _ctx.Chat.DeleteMessageAsync(Demo.Channel1, message.Id, As.Viewer3)).Ok);
            Assert.IsTrue((await _ctx.Chat.TimeoutAsync(Demo.Channel1, Demo.Viewer1, 60, null, As.Viewer3)).Ok);
            Assert.IsFalse(await _ctx.Chat.CanModerateAsync(Demo.Channel2, As.Viewer3), "Solo en el canal donde es moderador.");
        }

        [TestMethod]
        public async Task UnModerador_NoPuedeSancionarAOtroModerador_PeroElDuenoSi()
        {
            _ctx.MakeModerator(Demo.Viewer2);
            _ctx.MakeModerator(Demo.Viewer3);

            var byMod = await _ctx.Chat.BanAsync(Demo.Channel1, Demo.Viewer2, "x", As.Viewer3);
            var byOwner = await _ctx.Chat.BanAsync(Demo.Channel1, Demo.Viewer2, "x", As.Owner);

            Assert.AreEqual("Un moderador no puede sancionar a otro moderador.", byMod.Error);
            Assert.IsTrue(byOwner.Ok, byOwner.Error);
            Assert.AreEqual(ChannelRoleType.None, await _ctx.Chat.GetChannelRoleAsync(Demo.Channel1, Demo.Viewer2), "Un baneado deja de ser moderador.");
        }

        [TestMethod]
        public async Task UnVip_NoModera()
        {
            _ctx.MakeVip(Demo.Viewer2);

            Assert.IsFalse(await _ctx.Chat.CanModerateAsync(Demo.Channel1, As.Viewer2));
            Assert.AreEqual("command_error", (await _ctx.Send(As.Viewer2, "/clear")).ErrorCode);
        }

        [TestMethod]
        public async Task Insignias_ModeradorYVip()
        {
            _ctx.MakeModerator(Demo.Viewer2);
            _ctx.MakeVip(Demo.Viewer3);

            CollectionAssert.AreEqual(new[] { "moderator" }, (await _ctx.Send(As.Viewer2, "hola")).Message!.Badges);
            CollectionAssert.AreEqual(new[] { "vip" }, (await _ctx.Send(As.Viewer3, "hola")).Message!.Badges);
        }

        // ── Enlaces, solo emotes y repetidos ─────────────────────────────

        [TestMethod]
        [DataRow("mira https://estafa.example/gratis")]
        [DataRow("entra a www.algo.cl")]
        [DataRow("sigan mi-canal.tv")]
        public async Task Enlaces_BloqueadosPorDefecto(string text)
        {
            Assert.AreEqual("links_blocked", (await _ctx.Send(As.Viewer1, text)).ErrorCode);
        }

        [TestMethod]
        public async Task Enlaces_LosVipPuedenYSePuedenPermitirParaTodos()
        {
            _ctx.MakeVip(Demo.Viewer2);
            Assert.IsTrue((await _ctx.Send(As.Viewer2, "mi clip: https://clips.example.com/1")).Ok, "VIP");

            _ctx.ConfigureChannel(Demo.Channel1, f => f.BlockLinks = false);
            var other = _ctx.CreateService(); // caché nueva para leer la configuración nueva
            Assert.IsTrue((await other.SendAsync(As.Viewer1, Demo.Channel1, "https://example.com")).Ok);
        }

        [TestMethod]
        public async Task SoloEmotes_RechazaTextoPeroAceptaEmotes()
        {
            _ctx.ConfigureChannel(Demo.Channel1, f => f.EmoteOnlyMode = true);

            Assert.AreEqual("emote_only", (await _ctx.Send(As.Viewer1, "hola pepega")).ErrorCode);
            Assert.IsTrue((await _ctx.Send(As.Viewer1, "pepega :kappa:")).Ok);
            Assert.IsTrue((await _ctx.Send(As.Owner, "texto del dueño")).Ok, "Los moderadores están exentos.");
        }

        [TestMethod]
        public async Task MensajeRepetido_SeRechazaDurante30Segundos()
        {
            Assert.IsTrue((await _ctx.Send(As.Viewer1, "Hola")).Ok);

            Assert.AreEqual("duplicate", (await _ctx.Send(As.Viewer1, "hola")).ErrorCode, "Sin importar mayúsculas.");
            Assert.IsTrue((await _ctx.Send(As.Viewer2, "hola")).Ok, "Otro usuario sí puede decir lo mismo.");

            _ctx.Clock.AdvanceSeconds(30);
            Assert.IsTrue((await _ctx.Send(As.Viewer1, "hola")).Ok);
        }

        // ── AutoMod ──────────────────────────────────────────────────────

        private void EnableAutoMod() => _ctx.ConfigureChannel(Demo.Channel1, f => f.AutoModEnabled = true);

        [TestMethod]
        public async Task AutoMod_RetieneElMensajeDudosoSinPublicarlo()
        {
            EnableAutoMod();
            _ctx.AutoMod.FlagWhenContains("insulto", "tonto");

            var result = await _ctx.Send(As.Viewer1, "eres tonto");

            Assert.IsTrue(result.Ok);
            Assert.IsTrue(result.HeldForReview);
            Assert.IsNull(result.Message, "No se difunde.");
            Assert.AreEqual(0, _ctx.Db.MessagesOf(Demo.Channel1).Count);

            var held = result.Effects.OfType<AutoModHeldEffect>().Single().Held;
            Assert.AreEqual("eres tonto", held.Text);
            Assert.AreEqual("insulto", held.Category);
            Assert.AreEqual(1, _ctx.Chat.GetHeldMessages(Demo.Channel1).Count);
        }

        [TestMethod]
        public async Task AutoMod_DesactivadoEnElCanal_NoConsultaAlClasificador()
        {
            _ctx.AutoMod.FlagWhenContains("insulto", "tonto");

            Assert.IsTrue((await _ctx.Send(As.Viewer1, "eres tonto")).Message != null);
            Assert.AreEqual(0, _ctx.AutoMod.Received.Count);
        }

        [TestMethod]
        public async Task AutoMod_NoRevisaAModeradoresNiVip()
        {
            EnableAutoMod();
            _ctx.AutoMod.FlagWhenContains("insulto", "tonto");
            _ctx.MakeVip(Demo.Viewer2);

            Assert.IsNotNull((await _ctx.Send(As.Viewer2, "tonto el que lee")).Message);
            Assert.IsNotNull((await _ctx.Send(As.Owner, "tonto el que lee")).Message);
            Assert.AreEqual(0, _ctx.AutoMod.Received.Count);
        }

        [TestMethod]
        public async Task AutoMod_SiElServicioNoRespondeATiempo_ElMensajePasa()
        {
            EnableAutoMod();
            _ctx.AutoMod.FlagWhenContains("insulto", "tonto");
            _ctx.AutoMod.Delay = TimeSpan.FromSeconds(10); // el límite de la prueba es 300 ms

            var started = DateTime.UtcNow;
            var result = await _ctx.Send(As.Viewer1, "eres tonto");

            Assert.IsNotNull(result.Message, "Falla abierto: el chat no se congela.");
            Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        }

        [TestMethod]
        public async Task AutoMod_SiElServicioEstaCaido_ElMensajePasa()
        {
            EnableAutoMod();
            _ctx.AutoMod.Throw = true;

            Assert.IsNotNull((await _ctx.Send(As.Viewer1, "hola")).Message);
        }

        [TestMethod]
        public async Task AutoMod_AprobarPublicaElMensaje()
        {
            EnableAutoMod();
            _ctx.AutoMod.FlagWhenContains("insulto", "tonto");
            var held = (await _ctx.Send(As.Viewer1, "qué tonto ese jefe")).Effects.OfType<AutoModHeldEffect>().Single().Held;

            var result = await _ctx.Chat.ResolveAutoModAsync(Demo.Channel1, held.Id, approve: true, As.Owner);

            Assert.IsTrue(result.Ok, result.Error);
            var published = result.Effects.OfType<BroadcastMessageEffect>().Single().Message;
            Assert.AreEqual("qué tonto ese jefe", published.PlainText());
            Assert.AreEqual(1, _ctx.Db.MessagesOf(Demo.Channel1).Count);
            Assert.IsTrue(result.Effects.OfType<AutoModResolvedEffect>().Single().Approved);
        }

        [TestMethod]
        public async Task AutoMod_RechazarLoDescarta()
        {
            EnableAutoMod();
            _ctx.AutoMod.FlagWhenContains("insulto", "tonto");
            var held = (await _ctx.Send(As.Viewer1, "eres tonto")).Effects.OfType<AutoModHeldEffect>().Single().Held;

            var result = await _ctx.Chat.ResolveAutoModAsync(Demo.Channel1, held.Id, approve: false, As.Owner);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(0, result.Effects.OfType<BroadcastMessageEffect>().Count());
            Assert.AreEqual(0, _ctx.Db.MessagesOf(Demo.Channel1).Count);

            var again = await _ctx.Chat.ResolveAutoModAsync(Demo.Channel1, held.Id, approve: true, As.Owner);
            Assert.AreEqual("Ese mensaje ya fue revisado o expiró.", again.Error);
        }

        [TestMethod]
        public async Task AutoMod_UnViewerNoPuedeResolver()
        {
            EnableAutoMod();
            _ctx.AutoMod.FlagWhenContains("insulto", "tonto");
            var held = (await _ctx.Send(As.Viewer1, "eres tonto")).Effects.OfType<AutoModHeldEffect>().Single().Held;

            var result = await _ctx.Chat.ResolveAutoModAsync(Demo.Channel1, held.Id, approve: true, As.Viewer2);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, _ctx.Chat.GetHeldMessages(Demo.Channel1).Count, "Sigue esperando a un moderador.");
        }

        // ── Comandos ─────────────────────────────────────────────────────

        [TestMethod]
        public async Task Comando_Timeout_ConDuracionYMotivo()
        {
            var result = await _ctx.Send(As.Owner, $"/timeout @{Demo.Viewer1Name} 10m por spam");

            Assert.IsTrue(result.Ok, result.ErrorMessage);
            var sanction = result.Effects.OfType<UserSanctionedEffect>().Single();
            Assert.AreEqual(600, sanction.Seconds);
            Assert.AreEqual("por spam", _ctx.Db.Bans[(Demo.Channel1, Demo.Viewer1)].Reason);
        }

        [TestMethod]
        public async Task Comando_DeModeradorUsadoPorUnViewer_SeRechaza()
        {
            var result = await _ctx.Send(As.Viewer1, $"/ban {Demo.Viewer2Name}");

            Assert.AreEqual("command_error", result.ErrorCode);
            Assert.AreEqual("Solo los moderadores pueden usar /ban.", result.ErrorMessage);
            Assert.AreEqual(0, _ctx.Db.Bans.Count);
        }

        [TestMethod]
        public async Task Comando_Mod_SoloLoPuedeUsarElDueno()
        {
            _ctx.MakeModerator(Demo.Viewer3);

            Assert.AreEqual("Solo el dueño del canal puede usar /mod.", (await _ctx.Send(As.Viewer3, $"/mod {Demo.Viewer2Name}")).ErrorMessage);
            Assert.IsTrue((await _ctx.Send(As.Owner, $"/mod {Demo.Viewer2Name}")).Ok);
            Assert.AreEqual(ChannelRoleType.Moderator, await _ctx.Chat.GetChannelRoleAsync(Demo.Channel1, Demo.Viewer2));
        }

        [TestMethod]
        public async Task Comando_Me_EsUnMensajeDeAccion()
        {
            var result = await _ctx.Send(As.Viewer1, "/me saluda a todos");

            Assert.IsTrue(result.Message!.IsAction);
            Assert.AreEqual("saluda a todos", result.Message.PlainText());
        }

        [TestMethod]
        public async Task Comando_Desconocido_DiceComoVerLaAyuda()
        {
            StringAssert.Contains((await _ctx.Send(As.Viewer1, "/volar")).ErrorMessage, "/help");
        }

        [TestMethod]
        public async Task Comando_Slow_ActivaElModoLento()
        {
            var result = await _ctx.Send(As.Owner, "/slow 20");

            Assert.IsTrue(result.Ok, result.ErrorMessage);
            Assert.AreEqual(20, result.Effects.OfType<SettingsChangedEffect>().Single().Settings.SlowModeSeconds);
            Assert.IsTrue((await _ctx.Send(As.Viewer1, "uno")).Ok);
            Assert.AreEqual("slow_mode", (await _ctx.Send(As.Viewer1, "dos")).ErrorCode);
        }

        // ── Respuestas y mensaje fijado ──────────────────────────────────

        [TestMethod]
        public async Task Responder_GuardaAQuienSeRespondio()
        {
            var original = (await _ctx.Send(As.Viewer1, "¿qué juego es?")).Message!;

            var reply = (await _ctx.Send(As.Viewer2, "Elden Ring", replyToId: original.Id)).Message!;

            Assert.IsNotNull(reply.ReplyTo);
            Assert.AreEqual(original.Id, reply.ReplyTo.MessageId);
            Assert.AreEqual(Demo.Viewer1Name, reply.ReplyTo.DisplayName);
            Assert.AreEqual("¿qué juego es?", reply.ReplyTo.Text);
        }

        [TestMethod]
        public async Task Fijar_SoloModeradores()
        {
            var message = (await _ctx.Send(As.Viewer1, "importante")).Message!;

            Assert.IsFalse((await _ctx.Chat.PinAsync(Demo.Channel1, message.Id, As.Viewer2)).Ok);
            Assert.IsTrue((await _ctx.Chat.PinAsync(Demo.Channel1, message.Id, As.Owner)).Ok);
            Assert.AreEqual(message.Id, (await _ctx.Chat.GetPinnedAsync(Demo.Channel1))!.Id);
        }
    }
}
