using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Chat
{
    // Configuración del chat desde el Dashboard (modo lento, solo seguidores, palabras bloqueadas).
    [TestClass]
    public class ChatServiceSettingsTests
    {
        private ChatTestContext _ctx = null!;

        [TestInitialize]
        public void Setup() => _ctx = new ChatTestContext();

        // Lo que manda el formulario del Dashboard (lo demás queda como viene por defecto).
        private Task Save(int slowModeSeconds, bool followersOnly, IEnumerable<string> bannedWords) =>
            _ctx.Chat.UpdateSettingsAsync(Demo.Channel1, new ChatModerationSettingsDto
            {
                SlowModeSeconds = slowModeSeconds,
                FollowersOnly = followersOnly,
                BannedWords = bannedWords.ToList()
            });

        [TestMethod]
        public async Task GuardarConfiguracion_SeLeeIgualDespues()
        {
            await Save(30, true, new[] { "spam" });

            var settings = await _ctx.Chat.GetModerationSettingsAsync(Demo.Channel1);
            Assert.AreEqual(30, settings.SlowModeSeconds);
            Assert.IsTrue(settings.FollowersOnly);
            CollectionAssert.AreEqual(new[] { "spam" }, settings.BannedWords);
        }

        [TestMethod]
        [DataRow(-5, 0)]
        [DataRow(0, 0)]
        [DataRow(45, 45)]
        [DataRow(9999, 600)]
        public async Task ModoLento_SeAcotaEntre0Y10Minutos(int requested, int expected)
        {
            await Save(requested, false, Array.Empty<string>());

            Assert.AreEqual(expected, (await _ctx.Chat.GetSettingsAsync(Demo.Channel1)).SlowModeSeconds);
            Assert.AreEqual(expected > 0, _ctx.Db.FilterFor(Demo.Channel1).SlowModeEnabled);
        }

        [TestMethod]
        public async Task PalabrasBloqueadas_SeNormalizanYOrdenan()
        {
            var input = new[] { "  Tonto ", "TONTO", "", "   ", "feo", new string('x', 51) };

            await Save(0, false, input);

            var words = (await _ctx.Chat.GetModerationSettingsAsync(Demo.Channel1)).BannedWords;
            CollectionAssert.AreEqual(new[] { "feo", "tonto" }, words, "Minúsculas, sin espacios, sin repetidas, sin vacías ni de más de 50 letras.");
        }

        [TestMethod]
        public async Task PalabrasBloqueadas_LaListaNuevaReemplazaALaAnterior()
        {
            await Save(0, false, new[] { "uno", "dos" });
            await Save(0, false, new[] { "dos", "tres" });

            var words = (await _ctx.Chat.GetModerationSettingsAsync(Demo.Channel1)).BannedWords;
            CollectionAssert.AreEqual(new[] { "dos", "tres" }, words);
        }

        [TestMethod]
        public async Task PalabrasBloqueadas_MaximoDoscientas()
        {
            var many = Enumerable.Range(1, 250).Select(i => $"palabra{i}");

            await Save(0, false, many);

            Assert.AreEqual(200, (await _ctx.Chat.GetModerationSettingsAsync(Demo.Channel1)).BannedWords.Count);
        }

        [TestMethod]
        public async Task CambiarLaConfiguracion_RigeAlInstante()
        {
            // El primer mensaje deja la configuración del canal en caché (30 s).
            Assert.IsTrue((await _ctx.Send(As.Viewer1, "hola")).Ok);

            await Save(30, false, new[] { "spam" });

            Assert.AreEqual("slow_mode", (await _ctx.Send(As.Viewer1, "otra vez")).ErrorCode);
            Assert.AreEqual("blocked_term", (await _ctx.Send(As.Viewer2, "compren spam")).ErrorCode);
        }

        [TestMethod]
        public async Task ConfiguracionPorDefecto_SinModosEspeciales()
        {
            var settings = await _ctx.Chat.GetSettingsAsync(Demo.Channel2);

            Assert.AreEqual(0, settings.SlowModeSeconds);
            Assert.IsFalse(settings.FollowersOnly);
        }

        [TestMethod]
        [DataRow(1, "1 s")]
        [DataRow(59, "59 s")]
        [DataRow(60, "1 min")]
        [DataRow(90, "2 min")]
        [DataRow(600, "10 min")]
        [DataRow(7200, "2 h")]
        [DataRow(2 * 86400, "2 días")]
        public void FormatoDeDuraciones_ParaLosMensajesAlUsuario(int seconds, string expected)
        {
            Assert.AreEqual(expected, ChatService.FormatDuration(seconds));
        }
    }
}
