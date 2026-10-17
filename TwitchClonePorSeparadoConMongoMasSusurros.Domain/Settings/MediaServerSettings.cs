namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings
{
    // Sección "MediaServer" de appsettings.json.
    //
    // Modelo de rutas (igual que Twitch/Kick: la clave secreta NO es la URL pública):
    //   OBS publica en   rtmp://localhost/live   con clave   {channelId}?key={StreamKey}
    //   → MediaMTX recibe el path "live/{channelId}" y la query "key=..."
    //   → MediaMTX le pregunta a la app (POST /api/media/auth) si la clave es válida
    //   Los viewers miran  http://localhost:8888/live/{channelId}/index.m3u8  (sin clave)
    public class MediaServerSettings
    {
        public const string SectionName = "MediaServer";

        // Primer segmento del path en MediaMTX. Debe coincidir con el final de IngestUrl.
        public string PathPrefix { get; set; } = "live";

        // Lo que el streamer pega en OBS como "Servidor".
        public string IngestUrl { get; set; } = "rtmp://localhost/live";

        // Servidor HLS de MediaMTX (hlsAddress).
        public string HlsBaseUrl { get; set; } = "http://localhost:8888";

        // API de control de MediaMTX (apiAddress). Se usa para expulsar publicadores
        // y para sincronizar el estado EN VIVO si se perdió algún webhook.
        public string ApiBaseUrl { get; set; } = "http://127.0.0.1:9997";

        // Secreto compartido con mediamtx.yml (authHTTPAddress y runOnAvailable/runOnUnavailable).
        public string HookSecret { get; set; } = string.Empty;

        // Revisa cada N segundos que IsLive en SQL coincida con lo que MediaMTX está transmitiendo.
        public bool ReconcileLiveState { get; set; } = true;
        public int ReconcileIntervalSeconds { get; set; } = 30;

        public string PathFor(int channelId) => $"{PathPrefix}/{channelId}";

        public string HlsUrlFor(int channelId) => $"{HlsBaseUrl.TrimEnd('/')}/{PathFor(channelId)}/index.m3u8";

        // Lo que el streamer pega en OBS como "Clave de transmisión".
        public static string ObsStreamKeyFor(int channelId, string streamKey) => $"{channelId}?key={streamKey}";

        // "live/12" → 12. Rechaza cualquier otra forma (subcarpetas, letras, etc.).
        public bool TryParseChannelId(string? path, out int channelId)
        {
            channelId = 0;
            if (string.IsNullOrWhiteSpace(path)) return false;

            var trimmed = path.Trim('/');
            var prefix = PathPrefix.Trim('/') + "/";
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) return false;

            var rest = trimmed[prefix.Length..];
            return rest.Length is > 0 and <= 9
                   && rest.All(char.IsAsciiDigit)
                   && int.TryParse(rest, out channelId)
                   && channelId > 0;
        }
    }
}
