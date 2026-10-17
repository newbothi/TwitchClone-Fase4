using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    // Endpoints que llama MediaMTX (reemplaza a StreamWebhookController).
    //
    //  POST /api/media/auth?secret=...                → authHTTPAddress: ¿puede publicar/leer?
    //  POST /api/media/hooks/available?path=..&secret=..   → runOnAvailable: el stream empezó
    //  POST /api/media/hooks/unavailable?path=..&secret=.. → runOnUnavailable: el stream terminó
    //
    // Todos exigen el secreto compartido (MediaServer:HookSecret = el de mediamtx.yml).
    // Antes eran GET sin protección: cualquiera que supiera una stream key podía
    // marcar un canal en vivo/offline desde el navegador.
    [ApiController]
    [Route("api/media")]
    public class MediaHooksController : ControllerBase
    {
        private readonly IStreamLifecycleService _lifecycle;
        private readonly MediaServerSettings _settings;
        private readonly IHubContext<StreamHub> _hub;
        private readonly ILogger<MediaHooksController> _logger;

        public MediaHooksController(
            IStreamLifecycleService lifecycle,
            IOptions<MediaServerSettings> settings,
            IHubContext<StreamHub> hub,
            ILogger<MediaHooksController> logger)
        {
            _lifecycle = lifecycle;
            _settings = settings.Value;
            _hub = hub;
            _logger = logger;
        }

        // MediaMTX llama aquí por cada intento de publicar (OBS) o de leer (viewers HLS).
        // Respuesta 2xx = permitido; cualquier otra cosa = rechazado.
        [HttpPost("auth")]
        public async Task<IActionResult> Auth([FromQuery] string? secret, [FromBody] MediaMtxAuthRequest request)
        {
            if (!IsTrustedCaller(secret)) return Unauthorized();

            switch (request.Action)
            {
                case "publish":
                {
                    if (!_settings.TryParseChannelId(request.Path, out var channelId))
                    {
                        _logger.LogWarning("Publicación rechazada: path inválido '{Path}' desde {Ip}.", request.Path, request.Ip);
                        return StatusCode(StatusCodes.Status403Forbidden);
                    }

                    var result = await _lifecycle.AuthorizePublishAsync(channelId, ExtractStreamKey(request));
                    if (result == PublishAuthorization.Allowed)
                    {
                        _logger.LogInformation("Publicación autorizada en el canal {ChannelId} ({Protocol}) desde {Ip}.", channelId, request.Protocol, request.Ip);
                        return Ok();
                    }

                    _logger.LogWarning("Publicación rechazada en el canal {ChannelId} desde {Ip}: {Reason}.", channelId, request.Ip, result);
                    return result == PublishAuthorization.MissingKey
                        ? Unauthorized()
                        : StatusCode(StatusCodes.Status403Forbidden);
                }

                case "read":
                    // Ver un stream es público (Fase 2+: canales solo para suscriptores).
                    return _settings.TryParseChannelId(request.Path, out _)
                        ? Ok()
                        : StatusCode(StatusCodes.Status403Forbidden);

                case "playback":
                case "api":
                case "metrics":
                case "pprof":
                    // Grabaciones y administración: solo desde la misma máquina.
                    return IsLoopback(request.Ip) ? Ok() : StatusCode(StatusCodes.Status403Forbidden);

                default:
                    return StatusCode(StatusCodes.Status403Forbidden);
            }
        }

        [HttpPost("hooks/available")]
        public async Task<IActionResult> StreamAvailable([FromQuery] string? path, [FromQuery] string? secret)
        {
            if (!IsTrustedCaller(secret)) return Unauthorized();
            if (!_settings.TryParseChannelId(path, out var channelId)) return BadRequest("path inválido");

            var change = await _lifecycle.MarkLiveAsync(channelId);
            if (change == null) return NotFound();

            if (change.Changed)
                await _hub.Clients.Group(StreamHub.ChannelGroup(channelId)).SendAsync("StreamStarted");

            return Ok(new { channelId, live = true, changed = change.Changed });
        }

        [HttpPost("hooks/unavailable")]
        public async Task<IActionResult> StreamUnavailable([FromQuery] string? path, [FromQuery] string? secret)
        {
            if (!IsTrustedCaller(secret)) return Unauthorized();
            if (!_settings.TryParseChannelId(path, out var channelId)) return BadRequest("path inválido");

            var change = await _lifecycle.MarkOfflineAsync(channelId);
            if (change == null) return NotFound();

            if (change.Changed)
                await _hub.Clients.Group(StreamHub.ChannelGroup(channelId)).SendAsync("StreamEnded");

            return Ok(new { channelId, live = false, changed = change.Changed });
        }

        // OBS manda la clave como query: rtmp://host/live/{id}?key=XXXX.
        // También se acepta ?pass=XXXX (campo "password") por si se configura así.
        private static string? ExtractStreamKey(MediaMtxAuthRequest request)
        {
            if (!string.IsNullOrEmpty(request.Query))
            {
                var query = QueryHelpers.ParseQuery(request.Query.StartsWith('?') ? request.Query : "?" + request.Query);
                if (query.TryGetValue("key", out var key) && !string.IsNullOrEmpty(key)) return key.ToString();
            }
            if (!string.IsNullOrEmpty(request.Password)) return request.Password;
            if (!string.IsNullOrEmpty(request.Token)) return request.Token;
            return null;
        }

        private bool IsTrustedCaller(string? providedSecret)
        {
            if (string.IsNullOrEmpty(_settings.HookSecret))
            {
                // Sin secreto configurado, solo se acepta a MediaMTX corriendo en la misma máquina.
                var remote = HttpContext.Connection.RemoteIpAddress;
                return remote != null && IPAddress.IsLoopback(remote);
            }

            if (string.IsNullOrEmpty(providedSecret)) return false;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(providedSecret),
                Encoding.UTF8.GetBytes(_settings.HookSecret));
        }

        private static bool IsLoopback(string? ip)
            => IPAddress.TryParse(ip, out var address) && IPAddress.IsLoopback(address);
    }

    // Cuerpo que manda MediaMTX a authHTTPAddress (ver docs de MediaMTX, "Authentication").
    public class MediaMtxAuthRequest
    {
        public string? User { get; set; }
        public string? Password { get; set; }
        public string? Token { get; set; }
        public string? Ip { get; set; }
        public string? Action { get; set; }
        public string? Path { get; set; }
        public string? Protocol { get; set; }
        public string? Id { get; set; }
        public string? Query { get; set; }
        public string? UserAgent { get; set; }
    }
}
