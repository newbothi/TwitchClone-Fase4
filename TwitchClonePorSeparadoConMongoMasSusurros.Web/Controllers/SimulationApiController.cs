using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Simulation;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    // API que usa la prueba de carga (proyecto LoadTest) para preparar las cuentas de bots.
    // Solo existe en Development con Simulation:BotApiKey configurada; si no, responde 404.
    [ApiController]
    [AllowAnonymous]
    [Route("api/simulation")]
    public class SimulationApiController : ControllerBase
    {
        private readonly IBotAccountService _bots;
        private readonly IChannelRepository _channels;
        private readonly SimulationSettings _settings;
        private readonly IWebHostEnvironment _env;

        public SimulationApiController(IBotAccountService bots, IChannelRepository channels, IOptions<SimulationSettings> settings, IWebHostEnvironment env)
        {
            _bots = bots;
            _channels = channels;
            _settings = settings.Value;
            _env = env;
        }

        // POST /api/simulation/bots?count=500   (header X-Bot-Key)
        [HttpPost("bots")]
        public async Task<IActionResult> EnsureBots([FromQuery] int count = 100, CancellationToken ct = default)
        {
            if (!BotKeyAuthenticationHandler.IsEnabled(_env, _settings)) return NotFound();
            if (!BotKeyAuthenticationHandler.KeyMatches(BotKeyAuthenticationHandler.ProvidedKey(Request), _settings.BotApiKey)) return Unauthorized();

            var bots = await _bots.EnsureBotsAsync(count, null, ct);
            return Ok(new
            {
                total = bots.Count,
                numbers = bots.Take(Math.Max(0, count)).Select(b => b.Number)
            });
        }

        // GET /api/simulation/channels   (header X-Bot-Key) → para elegir canal en la prueba de carga
        [HttpGet("channels")]
        public async Task<IActionResult> Channels()
        {
            if (!BotKeyAuthenticationHandler.IsEnabled(_env, _settings)) return NotFound();
            if (!BotKeyAuthenticationHandler.KeyMatches(BotKeyAuthenticationHandler.ProvidedKey(Request), _settings.BotApiKey)) return Unauthorized();

            var channels = await _channels.GetChannelSummariesAsync();
            return Ok(channels.Select(c => new { c.Id, c.Name, owner = c.OwnerDisplayName, c.IsLive }));
        }
    }
}
