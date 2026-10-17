using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Simulation
{
    // Miniaturas de los canales en vivo (como las tarjetas del inicio de Twitch).
    //
    // Cada pocos segundos revisa qué canales están EN VIVO y, para los que no tienen
    // miniatura o la tienen vieja, saca un fotograma con ffmpeg leyendo el stream desde
    // MediaMTX (RTMP; si falla, HLS) y lo guarda en wwwroot/thumbnails/{canal}.jpg.
    // Sirve para cualquier stream, simulado o de OBS.
    public sealed class ThumbnailService : BackgroundService
    {
        private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(15);

        private readonly IServiceScopeFactory _scopes;
        private readonly StreamSimulator _streams;
        private readonly SimulationSettings _settings;
        private readonly MediaServerSettings _media;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<ThumbnailService> _logger;

        public ThumbnailService(
            IServiceScopeFactory scopes,
            StreamSimulator streams,
            IOptions<SimulationSettings> settings,
            IOptions<MediaServerSettings> media,
            IWebHostEnvironment env,
            ILogger<ThumbnailService> logger)
        {
            _scopes = scopes;
            _streams = streams;
            _settings = settings.Value;
            _media = media.Value;
            _env = env;
            _logger = logger;
        }

        public static string FolderFor(IWebHostEnvironment env) => Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "thumbnails");

        // URL de la miniatura (con ?v= para que el navegador no muestre una vieja), o null si no hay.
        public static string? UrlFor(IWebHostEnvironment env, int channelId)
        {
            var file = new FileInfo(Path.Combine(FolderFor(env), $"{channelId}.jpg"));
            return file.Exists ? $"/thumbnails/{channelId}.jpg?v={file.LastWriteTimeUtc.Ticks:x}" : null;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_settings.ThumbnailIntervalSeconds <= 0) return;
            var maxAge = TimeSpan.FromSeconds(Math.Max(15, _settings.ThumbnailIntervalSeconds));
            var folder = FolderFor(_env);

            try
            {
                Directory.CreateDirectory(folder);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); // que la app termine de arrancar

                using var timer = new PeriodicTimer(CheckEvery);
                do
                {
                    try
                    {
                        await CaptureDueAsync(folder, maxAge, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "No se pudieron actualizar las miniaturas.");
                    }
                } while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) { }
        }

        private async Task CaptureDueAsync(string folder, TimeSpan maxAge, CancellationToken ct)
        {
            var probe = await _streams.ProbeAsync();
            if (!probe.Available) return;

            List<int> live;
            await using (var scope = _scopes.CreateAsyncScope())
            {
                live = (await scope.ServiceProvider.GetRequiredService<IChannelRepository>().GetLiveChannelIdsAsync()).ToList();
            }

            var due = live
                .Where(id =>
                {
                    var file = new FileInfo(Path.Combine(folder, $"{id}.jpg"));
                    return !file.Exists || DateTime.UtcNow - file.LastWriteTimeUtc >= maxAge;
                })
                .ToList();
            if (due.Count == 0) return;

            await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
                async (channelId, token) => await CaptureAsync(probe.Path, folder, channelId, token));
        }

        public async Task<bool> CaptureAsync(string ffmpegPath, string folder, int channelId, CancellationToken ct)
        {
            var target = Path.Combine(folder, $"{channelId}.jpg");
            var temp = Path.Combine(folder, $"{channelId}.{Guid.NewGuid():N}.tmp.jpg");

            var sources = new[]
            {
                $"{_media.IngestUrl.TrimEnd('/')}/{channelId}",   // RTMP (lectura pública, sin clave)
                _media.HlsUrlFor(channelId)                       // HLS como respaldo
            };

            try
            {
                foreach (var source in sources)
                {
                    var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
                    if (source.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-rw_timeout", "8000000" });
                    args.AddRange(new[] { "-i", source, "-frames:v", "1", "-an", "-vf", "scale=640:-2", "-q:v", "5", "-update", "1", "-f", "image2", temp });

                    var result = await FfmpegRunner.RunAsync(ffmpegPath, args, TimeSpan.FromSeconds(20), ct);
                    if (result.ExitCode == 0 && File.Exists(temp) && new FileInfo(temp).Length > 0)
                    {
                        // Reemplazo atómico: el navegador nunca ve un JPG a medio escribir.
                        File.Move(temp, target, overwrite: true);
                        return true;
                    }
                    _logger.LogDebug("Miniatura del canal {ChannelId} desde {Source} falló: {Error}", channelId, source, result.Error);
                }
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* se limpiará en la próxima */ }
            }
        }
    }
}
