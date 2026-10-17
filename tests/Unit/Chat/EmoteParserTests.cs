using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Chat
{
    // Cómo se parte un mensaje en texto + emotes (lo que el navegador dibuja).
    [TestClass]
    public class EmoteParserTests
    {
        private static readonly Dictionary<string, string> Lookup = EmoteParser.BuildLookup(new[]
        {
            new Emote { Code = "pepega", ImageUrl = "/emotes/pepega.png" },
            new Emote { Code = ":kappa:", ImageUrl = "/emotes/kappa.png" }
        });

        private static string Describe(IEnumerable<ChatFragment> fragments) =>
            string.Join("|", fragments.Select(f => f.Type == ChatFragment.EmoteType ? $"[{f.Text}→{f.EmoteUrl}]" : $"'{f.Text}'"));

        [TestMethod]
        public void SinEmotesDelCanal_TodoEsTexto()
        {
            var fragments = EmoteParser.Parse("hola pepega", new Dictionary<string, string>());

            Assert.AreEqual("'hola pepega'", Describe(fragments));
        }

        [TestMethod]
        public void MensajeVacio_NoTieneFragmentos()
        {
            Assert.AreEqual(0, EmoteParser.Parse("", Lookup).Count);
        }

        [TestMethod]
        public void EmoteEnMedio_SeparaElTextoAntesYDespues()
        {
            var fragments = EmoteParser.Parse("hola pepega que tal", Lookup);

            Assert.AreEqual("'hola '|[pepega→/emotes/pepega.png]|' que tal'", Describe(fragments));
        }

        [TestMethod]
        public void SoloEmotes_SinTextoEntreMedio()
        {
            var fragments = EmoteParser.Parse("pepega kappa", Lookup);

            Assert.AreEqual("[pepega→/emotes/pepega.png]|' '|[kappa→/emotes/kappa.png]", Describe(fragments));
        }

        [TestMethod]
        [DataRow("pepega")]
        [DataRow(":pepega:")]
        [DataRow("kappa")]
        [DataRow(":kappa:")]
        public void SeReconoceConYSinDosPuntos(string written)
        {
            var fragments = EmoteParser.Parse(written, Lookup);

            Assert.AreEqual(1, fragments.Count);
            Assert.AreEqual(ChatFragment.EmoteType, fragments[0].Type);
            Assert.AreEqual(written, fragments[0].Text, "Se muestra tal como lo escribió el usuario.");
        }

        [TestMethod]
        [DataRow("Pepega", DisplayName = "Mayúsculas distintas")]
        [DataRow("pepegaa", DisplayName = "Letra de más")]
        [DataRow("pepega!", DisplayName = "Pegado a un signo")]
        [DataRow("superpepega", DisplayName = "Dentro de otra palabra")]
        public void SoloPalabraCompletaYExacta_EsEmote(string written)
        {
            var fragments = EmoteParser.Parse(written, Lookup);

            Assert.AreEqual(1, fragments.Count);
            Assert.AreEqual(ChatFragment.TextType, fragments[0].Type);
        }

        [TestMethod]
        public void LaUrlDelEmote_SaleSiempreDeLaTablaNoDelUsuario()
        {
            var fragments = EmoteParser.Parse("<img src=x onerror=alert(1)> pepega", Lookup);

            var emote = fragments.Single(f => f.Type == ChatFragment.EmoteType);
            Assert.AreEqual("/emotes/pepega.png", emote.EmoteUrl);
            Assert.AreEqual("<img src=x onerror=alert(1)> ", fragments[0].Text, "El HTML queda como texto plano (el navegador lo escapa).");
        }

        [TestMethod]
        public void BuildLookup_IgnoraEmotesSinCodigoOSinImagen()
        {
            var lookup = EmoteParser.BuildLookup(new[]
            {
                new Emote { Code = "::", ImageUrl = "/emotes/x.png" },
                new Emote { Code = "   ", ImageUrl = "/emotes/y.png" },
                new Emote { Code = "sinimagen", ImageUrl = "  " },
                new Emote { Code = " lul ", ImageUrl = "/emotes/lul.png" }
            });

            CollectionAssert.AreEquivalent(new[] { "lul", ":lul:" }, lookup.Keys.ToList());
        }
    }
}
