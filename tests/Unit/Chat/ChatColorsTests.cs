using System.Text.RegularExpressions;
using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Chat
{
    [TestClass]
    public class ChatColorsTests
    {
        [TestMethod]
        public void ElMismoUsuario_SiempreTieneElMismoColor()
        {
            // Valor fijo: si alguien cambia el algoritmo por string.GetHashCode (que varía
            // en cada ejecución de .NET) esta prueba lo detecta.
            Assert.AreEqual(ChatColors.ForUser("u1"), ChatColors.ForUser("u1"));
            Assert.AreEqual(Expected("u1"), ChatColors.ForUser("u1"));
            Assert.AreEqual(Expected("8f0c6a8e-1d2b-4c3e-9f00-abcdef123456"), ChatColors.ForUser("8f0c6a8e-1d2b-4c3e-9f00-abcdef123456"));
        }

        [TestMethod]
        public void SiempreEsUnColorHexValido()
        {
            var hex = new Regex("^#[0-9A-F]{6}$");
            foreach (var id in Enumerable.Range(0, 500).Select(i => $"usuario-{i}").Append(""))
                Assert.IsTrue(hex.IsMatch(ChatColors.ForUser(id)), id);
        }

        [TestMethod]
        public void MuchosUsuarios_UsanVariosColoresDeLaPaleta()
        {
            var distinct = Enumerable.Range(0, 500).Select(i => ChatColors.ForUser($"usuario-{i}")).Distinct().Count();

            Assert.AreEqual(12, distinct, "Con 500 usuarios deberían salir los 12 colores de la paleta.");
        }

        // Re-implementación independiente de FNV-1a 32 bits para fijar el resultado esperado.
        private static string Expected(string userId)
        {
            string[] palette =
            {
                "#FF4A4A", "#4A9EFF", "#3FCF6A", "#FF8A3D", "#B07CFF", "#FF5FA2",
                "#2EC4B6", "#F2C230", "#7FD1FF", "#9BE15D", "#FF7B7B", "#C792EA"
            };
            uint hash = 2166136261;
            foreach (var c in userId)
                hash = unchecked((hash ^ c) * 16777619);
            return palette[hash % 12];
        }
    }
}
