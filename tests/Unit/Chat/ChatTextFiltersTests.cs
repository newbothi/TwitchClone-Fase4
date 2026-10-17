using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Chat
{
    // Fase 4 (a): las dos reglas del score_heuristic del microservicio Python original,
    // ahora como funciones puras de C# (ChatText).
    [TestClass]
    public class ChatTextFiltersTests
    {
        // ── Exceso de mayúsculas: más de 10 letras y más del 80 % en mayúsculas ──

        [TestMethod]
        [DataRow("ESTO ES UN GRITO", DisplayName = "Todo en mayúsculas")]
        [DataRow("QUÉ PARTIDAZO ÑOÑO", DisplayName = "Con tildes y eñe")]
        [DataRow("JAJAJAJA QUE BUENO jaj", DisplayName = "Más del 80 %")]
        [DataRow("ABCDEFGHIJK", DisplayName = "Justo 11 letras")]
        public void Mayusculas_SeDetectan(string text)
        {
            Assert.IsTrue(ChatText.HasExcessiveCaps(text), text);
        }

        [TestMethod]
        [DataRow("hola a todos", DisplayName = "Normal")]
        [DataRow("GG WP", DisplayName = "Corto: 4 letras")]
        [DataRow("ABCDEFGHIJ", DisplayName = "Justo 10 letras: no alcanza")]
        [DataRow("ABCDEFGHijklmnopqrst", DisplayName = "40 % en mayúsculas")]
        [DataRow("ABCDEFGHIJKLMNOPqrst", DisplayName = "Exactamente 80 %: no supera")]
        [DataRow("JAJA 1234567890 !!!!", DisplayName = "Números y signos no son letras")]
        [DataRow("", DisplayName = "Vacío")]
        public void Mayusculas_NoSeDetectan(string text)
        {
            Assert.IsFalse(ChatText.HasExcessiveCaps(text), text);
        }

        [TestMethod]
        public void Mayusculas_LasMencionesYLosEnlacesNoCuentan()
        {
            Assert.IsFalse(ChatText.HasExcessiveCaps("@STREAMERUNO @VIEWERDOS hola"), "Nombres de usuario en mayúsculas.");
            Assert.IsFalse(ChatText.HasExcessiveCaps("mira WWW.EJEMPLO.COM ahora"), "Un enlace no es gritar.");
        }

        [TestMethod]
        public void Mayusculas_LosEmotesNoCuentan()
        {
            var fragments = new List<ChatFragment>
            {
                ChatFragment.FromEmote("KEKW", "/emotes/kekw.png"),
                ChatFragment.FromText(" "),
                ChatFragment.FromEmote("POGGERS", "/emotes/poggers.png"),
                ChatFragment.FromText(" que buena jugada")
            };

            Assert.IsFalse(ChatText.HasExcessiveCaps(fragments));
        }

        // ── Caracteres repetidos: 7 o más iguales seguidos ───────────────

        [TestMethod]
        [DataRow("holaaaaaaa", DisplayName = "Letra estirada")]
        [DataRow("!!!!!!!", DisplayName = "Signos")]
        [DataRow("NOOOOooo no", DisplayName = "Sin importar mayúsculas")]
        [DataRow("😂😂😂😂😂😂😂", DisplayName = "Emoji repetido")]
        [DataRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", DisplayName = "Muy largo")]
        public void Repetidos_SeDetectan(string text)
        {
            Assert.IsTrue(ChatText.HasRepeatedChars(text), text);
        }

        [TestMethod]
        [DataRow("holaaaaaa", DisplayName = "6 iguales: no alcanza")]
        [DataRow("jajajajajajajajaja", DisplayName = "Risa alternada")]
        [DataRow("gané 10000000 puntos", DisplayName = "Números")]
        [DataRow("a a a a a a a a", DisplayName = "Separados por espacios")]
        [DataRow("😂😂😂😂😂😂", DisplayName = "6 emojis")]
        [DataRow("", DisplayName = "Vacío")]
        public void Repetidos_NoSeDetectan(string text)
        {
            Assert.IsFalse(ChatText.HasRepeatedChars(text), text);
        }

        [TestMethod]
        public void Repetidos_SoloCuentaElTextoNoLosEmotes()
        {
            var fragments = new List<ChatFragment>
            {
                ChatFragment.FromEmote("aaaaaaaa", "/emotes/raro.png"),
                ChatFragment.FromText(" hola")
            };

            Assert.IsFalse(ChatText.HasRepeatedChars(fragments));
        }
    }
}
