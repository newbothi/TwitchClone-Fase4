using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MediaServer
{
    // Cliente de la API de control de MediaMTX (apiAddress en mediamtx.yml).
    // Probado contra MediaMTX v1.21. Soporta los nombres de endpoint nuevos
    // (/v3/rtmp/conns/...) y los antiguos (/v3/rtmpconns/...) por compatibilidad.
    public class MediaMtxClient : IMediaServerClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<MediaMtxClient> _logger;

        public MediaMtxClient(HttpClient http, IOptions<MediaServerSettings> settings, ILogger<MediaMtxClient> logger)
        {
            _http = http;
            _logger = logger;
            _http.BaseAddress ??= new Uri(settings.Value.ApiBaseUrl.TrimEnd('/') + "/");
            _http.Timeout = TimeSpan.FromSeconds(5);
        }

        public async Task<IReadOnlySet<string>?> GetAvailablePathsAsync(CancellationToken ct = default)
        {
            try
            {
                using var response = await _http.GetAsync("v3/paths/list?itemsPerPage=1000", ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("La API de MediaMTX respondió {Status} al listar paths.", (int)response.StatusCode);
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var result = new HashSet<string>(StringComparer.Ordinal);
                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("name", out var nameProp)) continue;

                        // "available" en versiones nuevas; "ready" (deprecado) en las anteriores.
                        var available =
                            (item.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True) ||
                            (item.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True);

                        if (available && nameProp.GetString() is { } name) result.Add(name);
                    }
                }
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogDebug(ex, "No se pudo contactar la API de MediaMTX en {BaseAddress}.", _http.BaseAddress);
                return null;
            }
        }

        public async Task<bool> KickPublisherAsync(string path, CancellationToken ct = default)
        {
            try
            {
                using var response = await _http.GetAsync($"v3/paths/get/{path}", ct);
                if (response.StatusCode == HttpStatusCode.NotFound) return false; // nadie publicando
                if (!response.IsSuccessStatusCode) return false;

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
                    return false;

                var type = source.TryGetProperty("type", out var t) ? t.GetString() : null;
                var id = source.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (type == null || id == null) return false;

                var (current, legacy) = type switch
                {
                    "rtmpConn" => ("rtmp/conns", "rtmpconns"),
                    "rtmpsConn" => ("rtmps/conns", "rtmpsconns"),
                    "srtConn" => ("srt/conns", "srtconns"),
                    "webRTCSession" => ("webrtc/sessions", "webrtcsessions"),
                    "rtspSession" => ("rtsp/sessions", "rtspsessions"),
                    "rtspsSession" => ("rtsps/sessions", "rtspssessions"),
                    _ => (null, null)
                };
                if (current == null) return false;

                using var kick = await _http.PostAsync($"v3/{current}/kick/{id}", content: null, ct);
                if (kick.IsSuccessStatusCode) return true;

                using var kickLegacy = await _http.PostAsync($"v3/{legacy}/kick/{id}", content: null, ct);
                return kickLegacy.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogWarning(ex, "No se pudo expulsar al publicador del path {Path} vía la API de MediaMTX.", path);
                return false;
            }
        }
    }
}
