using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Simulation;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    // Panel del simulador (Admin → Simulador). Reemplaza a la antigua página
    // "Simular viewers", que abría conexiones desde la pestaña del admin.
    //
    // Las acciones responden JSON: la página las llama con fetch y refresca el estado
    // cada 2 s, sin recargar.
    [Authorize(Roles = "Admin")]
    [AutoValidateAntiforgeryToken]
    public class SimulatorController : Controller
    {
        private readonly SimulationManager _manager;
        private readonly StreamSimulator _streams;
        private readonly IChannelRepository _channels;
        private readonly SimulationSettings _settings;
        private readonly MediaServerSettings _media;

        public SimulatorController(
            SimulationManager manager,
            StreamSimulator streams,
            IChannelRepository channels,
            IOptions<SimulationSettings> settings,
            IOptions<MediaServerSettings> media)
        {
            _manager = manager;
            _streams = streams;
            _channels = channels;
            _settings = settings.Value;
            _media = media.Value;
        }

        public async Task<IActionResult> Index()
        {
            await _streams.ProbeAsync();
            var model = new SimulatorPageModel
            {
                Settings = _settings,
                IngestUrl = _media.IngestUrl,
                MaxBots = _manager.MaxBots,
                Channels = (await _channels.GetChannelSummariesAsync()).ToList()
            };
            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Status()
        {
            var channels = await _channels.GetChannelSummariesAsync();
            return Json(new
            {
                status = _manager.GetStatus(),
                channels = channels.Select(c => new { c.Id, c.Name, owner = c.OwnerDisplayName, category = c.CategoryName, c.IsLive, c.ViewerCount, suspended = c.OwnerIsSuspended })
            });
        }

        [HttpPost]
        public IActionResult CreateBots(int count) => Result(_manager.CreateBots(count), "Creando cuentas de bots…");

        [HttpPost]
        public async Task<IActionResult> StartStream(int channelId) =>
            Result(await _streams.StartAsync(channelId), "Stream simulado iniciado. MediaMTX lo marcará EN VIVO en unos segundos.");

        [HttpPost]
        public async Task<IActionResult> StopStream(int channelId)
        {
            await _streams.StopAsync(channelId);
            return Result(null, "Stream simulado detenido.");
        }

        // activityPercent: 100 = normal (entero para no depender del separador decimal de la cultura).
        [HttpPost]
        public async Task<IActionResult> StartChat(int channelId, int viewers, int activityPercent = 100) =>
            Result(await _manager.StartChatAsync(channelId, viewers, activityPercent / 100.0), "Chat simulado en marcha.");

        [HttpPost]
        public async Task<IActionResult> StopChat(int channelId)
        {
            await _manager.StopChatAsync(channelId);
            return Result(null, "Chat simulado detenido.");
        }

        [HttpPost]
        public IActionResult StartFull(int channels, int viewers, int activityPercent = 100, bool withStreams = true) =>
            Result(_manager.StartFullSimulation(channels, viewers, activityPercent / 100.0, withStreams), "Preparando la simulación completa…");

        [HttpPost]
        public async Task<IActionResult> StopAll()
        {
            await _manager.StopAllAsync();
            return Result(null, "Se detuvieron todas las simulaciones.");
        }

        private IActionResult Result(string? error, string okMessage) =>
            error == null
                ? Json(new { ok = true, message = okMessage })
                : Json(new { ok = false, message = error });
    }
}
