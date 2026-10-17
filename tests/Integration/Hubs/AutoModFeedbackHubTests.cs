using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Ai;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Integration.Hubs
{
    // Fase 4 (d): AutoMod con machine learning visto desde el chat.
    [TestClass]
    public class AutoModFeedbackHubTests
    {
        private AppUnderTest _app = null!;

        [TestCleanup]
        public async Task Stop()
        {
            if (_app != null) await _app.DisposeAsync();
        }

        [TestMethod]
        public async Task LasDecisionesDelModeradorEnElChat_LleganALaColaDeFeedback()
        {
            _app = await AppUnderTest.StartAsync(arrange: db =>
            {
                db.FilterFor(Demo.Channel1).AutoModEnabled = true;
                db.AutoMod.FlagWhenContains("insulto", "tonto");
            });
            var owner = await _app.JoinAsync(Demo.Channel1, Demo.Owner1);
            var viewer = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);

            await viewer.SendAsync(Demo.Channel1, "qué tonto ese jefe");
            var held = (await owner.WaitForAsync("AutoModHeld")).Arg<AutoModHeldDto>(0);
            await owner.ModerateAsync("ResolveAutoMod", Demo.Channel1, held.Id, true);

            var normal = (await viewer.SendAsync(Demo.Channel1, "mensaje que se borra")).Message!;
            await owner.ModerateAsync("DeleteMessage", Demo.Channel1, normal.Id);

            var items = _app.Db.AutoModFeedback.Items;
            Assert.AreEqual(2, items.Count);
            Assert.AreEqual(new AutoModFeedback(Demo.Channel1, "qué tonto ese jefe", false, "insulto", "automod_aprobado"), items[0]);
            Assert.AreEqual(new AutoModFeedback(Demo.Channel1, "mensaje que se borra", true, null, "mensaje_borrado"), items[1]);
        }

        // ── Contra el servicio Python de verdad (opcional) ────────────────
        // Se ejecuta solo si el servicio está corriendo y se indica dónde:
        //   PRUEBAS_AUTOMOD_URL=http://127.0.0.1:8001
        // (el workflow de GitHub lo levanta y define esta variable).

        private static string? PythonUrl => Environment.GetEnvironmentVariable("PRUEBAS_AUTOMOD_URL")?.TrimEnd('/');

        [TestMethod]
        [TestCategory("ServiciosPython")]
        public async Task ConElServicioPythonReal_RetieneAprendeYDejaPasarLoNormal()
        {
            if (PythonUrl == null)
                Assert.Inconclusive("Define PRUEBAS_AUTOMOD_URL (por ejemplo http://127.0.0.1:8001) con python/automod corriendo.");

            using var python = new HttpClient { BaseAddress = new Uri(PythonUrl + "/") };
            var feedbackBefore = (await python.GetFromJsonAsync<JsonElement>("model")).GetProperty("feedback").GetProperty("total").GetInt32();

            _app = await AppUnderTest.StartAsync(
                arrange: db => db.FilterFor(Demo.Channel1).AutoModEnabled = true,
                configureServices: services =>
                {
                    // Los clientes REALES de Infrastructure, apuntando al servicio Python.
                    services.Configure<AutoModSettings>(o =>
                    {
                        o.HttpUrl = PythonUrl + "/moderate";
                        o.FeedbackUrl = PythonUrl + "/feedback";
                        o.TimeoutMs = 5000;
                    });
                    services.AddHttpClient<HttpAutoModClassifier>();
                    services.Replace(ServiceDescriptor.Singleton<IAutoModClassifier>(sp => sp.GetRequiredService<HttpAutoModClassifier>()));
                    services.AddSingleton(sp => new HttpAutoModFeedbackSink(new HttpClient(),
                        sp.GetRequiredService<IOptions<AutoModSettings>>(), sp.GetRequiredService<ILogger<HttpAutoModFeedbackSink>>()));
                    services.Replace(ServiceDescriptor.Singleton<IAutoModFeedbackSink>(sp => sp.GetRequiredService<HttpAutoModFeedbackSink>()));
                    services.AddHostedService(sp => sp.GetRequiredService<HttpAutoModFeedbackSink>());
                });

            var owner = await _app.JoinAsync(Demo.Channel1, Demo.Owner1);
            var viewer = await _app.JoinAsync(Demo.Channel1, Demo.Viewer1);
            var other = await _app.JoinAsync(Demo.Channel1, Demo.Viewer2);

            var clean = await viewer.SendAsync(Demo.Channel1, "hola chat, qué buen stream");
            Assert.IsNotNull(clean.Message, "Un mensaje normal se publica.");

            var toxic = await other.SendAsync(Demo.Channel1, "eres un idiota y un inútil");
            Assert.IsTrue(toxic.HeldForReview, "El modelo de Python retiene el insulto.");

            var held = (await owner.WaitForAsync("AutoModHeld")).Arg<AutoModHeldDto>(0);
            Assert.AreEqual("insulto", held.Category);
            StringAssert.Contains(held.Reason, "Probable insulto");

            await owner.ModerateAsync("ResolveAutoMod", Demo.Channel1, held.Id, false);

            await Eventually.TrueAsync(() =>
            {
                var total = python.GetFromJsonAsync<JsonElement>("model").GetAwaiter().GetResult()
                    .GetProperty("feedback").GetProperty("total").GetInt32();
                return total == feedbackBefore + 1;
            }, "la decisión del moderador llega a /feedback del servicio Python");
        }
    }
}
