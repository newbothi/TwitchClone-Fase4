using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Ai;
using TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes;
using TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.AutoMod
{
    // Fase 4 (d): las decisiones de los moderadores llegan al servicio Python para que AutoMod aprenda.
    //   ChatService → AutoModFeedbackEffect → AutoModFeedbackObserver → IAutoModFeedbackSink → POST /feedback
    [TestClass]
    public class AutoModFeedbackTests
    {
        private ChatTestContext _ctx = null!;

        [TestInitialize]
        public void Setup()
        {
            _ctx = new ChatTestContext();
            _ctx.ConfigureChannel(Demo.Channel1, f => f.AutoModEnabled = true);
            _ctx.AutoMod.FlagWhenContains("insulto", "tonto");
        }

        private async Task<string> HoldAsync(string text) =>
            (await _ctx.Send(As.Viewer1, text)).Effects.OfType<AutoModHeldEffect>().Single().Held.Id;

        // ── Qué decisiones generan feedback (ChatService) ─────────────────

        [TestMethod]
        public async Task Aprobar_EsUnFalsoPositivo()
        {
            var heldId = await HoldAsync("qué tonto ese jefe");

            var result = await _ctx.Chat.ResolveAutoModAsync(Demo.Channel1, heldId, approve: true, As.Owner);

            var feedback = result.Effects.OfType<AutoModFeedbackEffect>().Single();
            Assert.AreEqual("qué tonto ese jefe", feedback.Text);
            Assert.IsFalse(feedback.IsToxic);
            Assert.AreEqual("insulto", feedback.Category, "Se manda la categoría que había puesto AutoMod.");
            Assert.AreEqual(AutoModFeedbackEffect.Approved, feedback.Source);
        }

        [TestMethod]
        public async Task Rechazar_ConfirmaQueEraToxico()
        {
            var heldId = await HoldAsync("eres tonto");

            var result = await _ctx.Chat.ResolveAutoModAsync(Demo.Channel1, heldId, approve: false, As.Owner);

            var feedback = result.Effects.OfType<AutoModFeedbackEffect>().Single();
            Assert.IsTrue(feedback.IsToxic);
            Assert.AreEqual("insulto", feedback.Category);
            Assert.AreEqual(AutoModFeedbackEffect.Rejected, feedback.Source);
        }

        [TestMethod]
        public async Task BorrarElMensajeDeOtro_EsUnEjemploToxicoSinCategoria()
        {
            var message = (await _ctx.Send(As.Viewer2, "mensaje que AutoMod dejó pasar")).Message!;

            var result = await _ctx.Chat.DeleteMessageAsync(Demo.Channel1, message.Id, As.Owner);

            var feedback = result.Effects.OfType<AutoModFeedbackEffect>().Single();
            Assert.AreEqual("mensaje que AutoMod dejó pasar", feedback.Text);
            Assert.IsTrue(feedback.IsToxic);
            Assert.IsNull(feedback.Category, "El servicio Python elige la categoría más probable.");
            Assert.AreEqual(AutoModFeedbackEffect.Deleted, feedback.Source);
        }

        [TestMethod]
        public async Task BorrarUnMensajePropio_NoEnseñaNada()
        {
            var own = (await _ctx.Send(As.Owner, "me equivoqué de link")).Message!;

            var result = await _ctx.Chat.DeleteMessageAsync(Demo.Channel1, own.Id, As.Owner);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(0, result.Effects.OfType<AutoModFeedbackEffect>().Count());
        }

        [TestMethod]
        public async Task UnaAccionRechazada_NoGeneraFeedback()
        {
            var heldId = await HoldAsync("eres tonto");

            var result = await _ctx.Chat.ResolveAutoModAsync(Demo.Channel1, heldId, approve: false, As.Viewer2); // no es moderador

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(0, result.Effects.Count);
        }

        // ── El observador (efecto → cola) ─────────────────────────────────

        [TestMethod]
        public async Task Observador_PasaElFeedbackALaColaConElCanal()
        {
            var sink = new RecordingAutoModFeedbackSink();
            var observer = new AutoModFeedbackObserver(sink);

            await observer.OnEffectsAsync(7, new ChatEffect[]
            {
                new MessageDeletedEffect("abc"),
                new AutoModFeedbackEffect("eres tonto", true, "insulto", AutoModFeedbackEffect.Rejected)
            });

            var item = sink.Items.Single();
            Assert.AreEqual(new AutoModFeedback(7, "eres tonto", true, "insulto", AutoModFeedbackEffect.Rejected), item);
        }

        [TestMethod]
        public async Task Observador_ConLaColaDesactivada_NoHaceNada()
        {
            var sink = new RecordingAutoModFeedbackSink { IsEnabled = false };

            await new AutoModFeedbackObserver(sink).OnEffectsAsync(1, new ChatEffect[]
            {
                new AutoModFeedbackEffect("x", true, null, AutoModFeedbackEffect.Deleted)
            });

            Assert.AreEqual(0, sink.Items.Count);
        }

        // ── El envío HTTP al servicio Python ──────────────────────────────

        private static (HttpAutoModFeedbackSink Sink, FakeHttpHandler Handler) CreateHttpSink(string feedbackUrl = "http://automod.test/feedback")
        {
            var handler = new FakeHttpHandler();
            var sink = new HttpAutoModFeedbackSink(
                new HttpClient(handler),
                Options.Create(new AutoModSettings { FeedbackUrl = feedbackUrl }),
                NullLogger<HttpAutoModFeedbackSink>.Instance,
                retryDelays: new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10) });
            return (sink, handler);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("No ocurrió a tiempo.");
                await Task.Delay(10);
            }
        }

        [TestMethod]
        public async Task Http_EnviaElJsonQueEsperaElServicioPython()
        {
            var (sink, handler) = CreateHttpSink();
            await sink.StartAsync(CancellationToken.None);

            sink.Enqueue(new AutoModFeedback(12, "eres tonto", true, "insulto", AutoModFeedbackEffect.Rejected));
            await WaitUntilAsync(() => sink.Sent == 1);
            await sink.StopAsync(CancellationToken.None);

            var request = handler.Requests.Single();
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("http://automod.test/feedback", request.Uri.ToString());
            using var json = JsonDocument.Parse(request.Body);
            var root = json.RootElement;
            Assert.AreEqual("eres tonto", root.GetProperty("text").GetString());
            Assert.IsTrue(root.GetProperty("isToxic").GetBoolean());
            Assert.AreEqual("insulto", root.GetProperty("category").GetString());
            Assert.AreEqual("automod_rechazado", root.GetProperty("source").GetString());
            Assert.AreEqual(12, root.GetProperty("channelId").GetInt32());
        }

        [TestMethod]
        public async Task Http_SiElServicioFalla_ReintentaYDespuesDescarta()
        {
            var (sink, handler) = CreateHttpSink();
            handler.Respond = _ => FakeHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
            await sink.StartAsync(CancellationToken.None);

            sink.Enqueue(new AutoModFeedback(1, "x", true, null, AutoModFeedbackEffect.Deleted));
            await WaitUntilAsync(() => sink.Dropped == 1);

            Assert.AreEqual(3, handler.Requests.Count, "1 intento + 2 reintentos.");

            // Y sigue funcionando para el siguiente.
            handler.Respond = _ => FakeHttpHandler.Json("{}");
            sink.Enqueue(new AutoModFeedback(1, "y", false, null, AutoModFeedbackEffect.Approved));
            await WaitUntilAsync(() => sink.Sent == 1);
            await sink.StopAsync(CancellationToken.None);
        }

        [TestMethod]
        public void Http_EncolarNuncaBloquea_AunqueElServicioNoEsteCorriendo()
        {
            var (sink, _) = CreateHttpSink(); // sin StartAsync: nadie vacía la cola

            var started = DateTime.UtcNow;
            for (var i = 0; i < 5000; i++)
                sink.Enqueue(new AutoModFeedback(1, $"mensaje {i}", true, null, AutoModFeedbackEffect.Deleted));

            Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(1), "Con la cola llena descarta los más viejos.");
        }

        [TestMethod]
        public void Http_SinUrlConfigurada_QuedaDesactivado()
        {
            var (sink, _) = CreateHttpSink(feedbackUrl: "");
            Assert.IsFalse(sink.IsEnabled);
        }
    }
}
