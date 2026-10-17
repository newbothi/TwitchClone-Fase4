using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Ai;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.AutoMod
{
    // El cliente C# del AutoMod HTTP debe entender lo que responde python/automod (/moderate).
    [TestClass]
    public class HttpAutoModClassifierTests
    {
        private static (HttpAutoModClassifier Classifier, FakeHttpHandler Handler) Create(string url = "http://automod.test/moderate")
        {
            var handler = new FakeHttpHandler();
            var classifier = new HttpAutoModClassifier(new HttpClient(handler), Options.Create(new AutoModSettings { HttpUrl = url }));
            return (classifier, handler);
        }

        [TestMethod]
        public async Task EntiendeLaRespuestaDelServicioPython()
        {
            var (classifier, handler) = Create();
            handler.Respond = _ => FakeHttpHandler.Json(
                """{"flagged": true, "category": "insulto", "reason": "Probable insulto (91 %)", "score": 0.91, "modelVersion": "20261016-150000"}""");

            var verdict = await classifier.ClassifyAsync("eres un idiota", CancellationToken.None);

            Assert.IsTrue(verdict.Flagged);
            Assert.AreEqual("insulto", verdict.Category);
            Assert.AreEqual("Probable insulto (91 %)", verdict.Reason);

            var request = handler.Requests.Single();
            Assert.AreEqual("http://automod.test/moderate", request.Uri.ToString());
            Assert.AreEqual("eres un idiota", JsonDocument.Parse(request.Body).RootElement.GetProperty("text").GetString());
        }

        [TestMethod]
        public async Task MensajeLimpio()
        {
            var (classifier, handler) = Create();
            handler.Respond = _ => FakeHttpHandler.Json("""{"flagged": false, "category": "ninguna", "reason": "", "score": 0.12}""");

            var verdict = await classifier.ClassifyAsync("hola", CancellationToken.None);

            Assert.IsFalse(verdict.Flagged);
            Assert.AreEqual("ninguna", verdict.Category);
        }

        [TestMethod]
        [DataRow("""{"flag": true}""", DisplayName = "flag")]
        [DataRow("""{"toxic": true}""", DisplayName = "toxic")]
        public async Task AceptaOtrosNombresDeCampo(string json)
        {
            var (classifier, handler) = Create();
            handler.Respond = _ => FakeHttpHandler.Json(json);

            var verdict = await classifier.ClassifyAsync("x", CancellationToken.None);

            Assert.IsTrue(verdict.Flagged);
            Assert.AreEqual("otro", verdict.Category);
        }

        [TestMethod]
        public async Task SiElServicioResponde500_LanzaYElChatFallaAbierto()
        {
            var (classifier, handler) = Create();
            handler.Respond = _ => FakeHttpHandler.Json("{}", HttpStatusCode.InternalServerError);

            // ChatService atrapa esta excepción y deja pasar el mensaje (ver ChatServicePhase2Tests).
            await Assert.ThrowsExceptionAsync<HttpRequestException>(() => classifier.ClassifyAsync("x", CancellationToken.None));
        }

        [TestMethod]
        public void SinUrl_NoEstaDisponible()
        {
            Assert.IsFalse(Create(url: "").Classifier.IsAvailable);
            Assert.IsTrue(Create().Classifier.IsAvailable);
        }
    }
}
