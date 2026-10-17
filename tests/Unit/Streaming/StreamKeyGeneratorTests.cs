using System.Text.RegularExpressions;
using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Streaming
{
    [TestClass]
    public class StreamKeyGeneratorTests
    {
        [TestMethod]
        public void TieneElFormatoEsperado()
        {
            var key = StreamKeyGenerator.NewKey();

            Assert.AreEqual(35, key.Length, key);
            Assert.IsTrue(key.StartsWith("sk_", StringComparison.Ordinal), key);
        }

        [TestMethod]
        public void SoloUsaCaracteresQueOBSYLasUrlNoRompen()
        {
            // Sin '+', '/', '=', '?', '&' ni espacios: la clave viaja como query "?key=..." hacia MediaMTX.
            var safe = new Regex("^sk_[A-Za-z0-9]{32}$");
            for (var i = 0; i < 1000; i++)
            {
                var key = StreamKeyGenerator.NewKey();
                Assert.IsTrue(safe.IsMatch(key), key);
            }
        }

        [TestMethod]
        public void NuncaSeRepite()
        {
            var keys = Enumerable.Range(0, 5000).Select(_ => StreamKeyGenerator.NewKey()).ToList();

            Assert.AreEqual(keys.Count, keys.Distinct().Count());
        }
    }
}
