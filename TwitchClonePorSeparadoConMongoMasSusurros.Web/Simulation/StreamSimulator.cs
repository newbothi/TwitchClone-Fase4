using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Simulation
{
    public sealed record FfmpegProbe(bool Available, string Path, string? Version, string? Error, bool HasDrawText)
    {
        public static readonly FfmpegProbe Unknown = new(false, "ffmpeg", null, "Todavía no se verificó ffmpeg.", false);
    }

    public sealed record SimulatedStreamStatus(int ChannelId, string State, string Source, DateTimeOffset? Since, int Restarts, string? LastError);

    // Transmite "como si fuera OBS" a MediaMTX con ffmpeg, uno por canal:
    //   ffmpeg → rtmp://localhost/live/{canal}?key={clave}
    // MediaMTX valida la clave con la app (igual que con OBS), el canal pasa a EN VIVO
    // por el webhook y los viewers lo ven por HLS. No hay ningún atajo: es el mismo
    // camino que recorre un streamer real.
    //
    // Fuente de video: los .mp4 de Simulation:VideosFolder (en bucle) o, si no hay,
    // señales generadas por ffmpeg (barras, fractales, juego de la vida...) con el
    // nombre del canal, la hora y el tiempo al aire impresos encima.
    public sealed class StreamSimulator : IHostedService
    {
        private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".mov", ".flv", ".webm" };

        private readonly IServiceScopeFactory _scopes;
        private readonly SimulationSettings _settings;
        private readonly MediaServerSettings _media;
        private readonly ILogger<StreamSimulator> _logger;
        private readonly ConcurrentDictionary<int, StreamRunner> _runners = new();
        private readonly SemaphoreSlim _probeGate = new(1, 1);
        private HashSet<string> _filters = new(StringComparer.Ordinal);
        private volatile FfmpegProbe _probe = FfmpegProbe.Unknown;
        private volatile bool _probed;
        private volatile bool _drawTextBroken;

        public StreamSimulator(
            IServiceScopeFactory scopes,
            IOptions<SimulationSettings> settings,
            IOptions<MediaServerSettings> media,
            ILogger<StreamSimulator> logger)
        {
            _scopes = scopes;
            _settings = settings.Value;
            _media = media.Value;
            _logger = logger;
        }

        public FfmpegProbe CachedProbe => _probe;

        public bool IsRunning(int channelId) => _runners.TryGetValue(channelId, out var r) && !r.Loop.IsCompleted;

        public IReadOnlyList<SimulatedStreamStatus> GetStatus() =>
            _runners.Values
                .OrderBy(r => r.ChannelId)
                .Select(r => new SimulatedStreamStatus(r.ChannelId, r.State, r.Source, r.Since, r.Restarts, r.LastError))
                .ToList();

        // ════════════════════════════════════════════════════════════════
        //  ffmpeg disponible
        // ════════════════════════════════════════════════════════════════

        public async Task<FfmpegProbe> ProbeAsync(bool refresh = false)
        {
            if (_probed && !refresh) return _probe;
            await _probeGate.WaitAsync();
            try
            {
                if (_probed && !refresh) return _probe;

                var path = string.IsNullOrWhiteSpace(_settings.FfmpegPath) ? "ffmpeg" : _settings.FfmpegPath;
                var version = await FfmpegRunner.RunAsync(path, new[] { "-hide_banner", "-version" }, TimeSpan.FromSeconds(10), CancellationToken.None, captureStdout: true);
                if (version.ExitCode != 0)
                {
                    _probe = new FfmpegProbe(false, path, null, version.Error ?? "ffmpeg no respondió.", false);
                }
                else
                {
                    var firstLine = version.Output.Split('\n', 2)[0].Trim();
                    var copyright = firstLine.IndexOf(" Copyright", StringComparison.Ordinal);
                    if (copyright > 0) firstLine = firstLine[..copyright];
                    var filters = await FfmpegRunner.RunAsync(path, new[] { "-hide_banner", "-filters" }, TimeSpan.FromSeconds(10), CancellationToken.None, captureStdout: true);
                    _filters = ParseFilters(filters.Output);
                    _probe = new FfmpegProbe(true, path, firstLine, null, _filters.Contains("drawtext"));
                }
                _probed = true;
                if (!_probe.Available) _logger.LogWarning("ffmpeg no está disponible ({Path}): {Error}", path, _probe.Error);
                return _probe;
            }
            finally
            {
                _probeGate.Release();
            }
        }

        // Salida de "ffmpeg -filters":  " T.C drawtext          V->V       Draw text..."
        private static HashSet<string> ParseFilters(string output)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in output.Split('\n'))
            {
                var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[2].Contains("->")) set.Add(parts[1]);
            }
            return set;
        }

        private bool HasFilter(string name) => _filters.Count == 0 || _filters.Contains(name);

        // ════════════════════════════════════════════════════════════════
        //  Iniciar / detener
        // ════════════════════════════════════════════════════════════════

        // Devuelve un mensaje de error para el panel, o null si arrancó.
        public async Task<string?> StartAsync(int channelId)
        {
            var probe = await ProbeAsync();
            if (!probe.Available)
                return $"No se encontró ffmpeg ({probe.Path}). Instálalo (winget install Gyan.FFmpeg) o configura Simulation:FfmpegPath.";

            if (IsRunning(channelId)) return null;

            await using (var scope = _scopes.CreateAsyncScope())
            {
                var info = await scope.ServiceProvider.GetRequiredService<IChannelRepository>().GetStreamInfoAsync(channelId);
                if (info == null) return "El canal no existe.";
                if (info.OwnerIsSuspended) return "El dueño del canal está suspendido: MediaMTX rechazaría la transmisión.";
            }

            var runner = new StreamRunner(channelId);
            _runners[channelId] = runner;
            runner.Loop = Task.Run(() => RunAsync(runner, runner.Cts.Token));
            _logger.LogInformation("Stream simulado iniciado en el canal {ChannelId}.", channelId);
            return null;
        }

        public async Task StopAsync(int channelId)
        {
            if (!_runners.TryRemove(channelId, out var runner)) return;
            runner.Cts.Cancel();
            try { await runner.Loop.WaitAsync(TimeSpan.FromSeconds(8)); } catch { /* ya se forzó el cierre */ }
            _logger.LogInformation("Stream simulado detenido en el canal {ChannelId}.", channelId);
        }

        public async Task StopAllAsync()
        {
            var ids = _runners.Keys.ToList();
            await Task.WhenAll(ids.Select(StopAsync));
        }

        Task IHostedService.StartAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(async () =>
            {
                try { await ProbeAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "No se pudo verificar ffmpeg."); }
            }, cancellationToken);
            return Task.CompletedTask;
        }

        // Al cerrar la app se cierran todos los ffmpeg (si no, quedarían transmitiendo solos).
        Task IHostedService.StopAsync(CancellationToken cancellationToken) => StopAllAsync();

        // ════════════════════════════════════════════════════════════════
        //  Proceso de ffmpeg con reconexión
        // ════════════════════════════════════════════════════════════════

        private async Task RunAsync(StreamRunner runner, CancellationToken ct)
        {
            var quickFailures = 0;
            var basicSource = false;

            while (!ct.IsCancellationRequested)
            {
                ChannelStreamInfo? info;
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    // Se lee la clave en cada intento: si el streamer la regenera, el simulador sigue funcionando.
                    info = await scope.ServiceProvider.GetRequiredService<IChannelRepository>().GetStreamInfoAsync(runner.ChannelId);
                }
                catch (Exception ex)
                {
                    runner.LastError = "No se pudo leer el canal: " + ex.Message;
                    runner.State = "reconectando";
                    if (!await DelayAsync(TimeSpan.FromSeconds(10), ct)) break;
                    continue;
                }

                if (info == null) { runner.Fail("El canal no existe."); break; }
                if (info.OwnerIsSuspended) { runner.Fail("El dueño del canal está suspendido."); break; }

                var url = $"{_media.IngestUrl.TrimEnd('/')}/{runner.ChannelId}?key={Uri.EscapeDataString(info.StreamKey)}";
                var (args, sourceName) = BuildArguments(runner.ChannelId, info, url, basicSource);
                runner.Source = sourceName;

                var startedAt = DateTimeOffset.UtcNow;
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo(_probe.Path)
                    {
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        CreateNoWindow = true
                    }
                };
                foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
                process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) runner.AddLog(e.Data); };

                try
                {
                    process.Start();
                    ChildProcessTracker.Track(process); // Windows: muere con la app aunque se cierre de golpe
                    process.BeginErrorReadLine();
                }
                catch (Exception ex)
                {
                    runner.Fail($"No se pudo ejecutar ffmpeg ({_probe.Path}): {ex.Message}");
                    break;
                }

                runner.State = "transmitiendo";
                runner.Since = startedAt;

                try
                {
                    await process.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    await FfmpegRunner.StopGracefullyAsync(process);
                    break;
                }

                var ran = DateTimeOffset.UtcNow - startedAt;
                var log = runner.TakeLog();
                runner.Restarts++;
                quickFailures = ran < TimeSpan.FromSeconds(15) ? quickFailures + 1 : 0;

                if (!_drawTextBroken && (log.Contains("drawtext", StringComparison.OrdinalIgnoreCase) || log.Contains("font", StringComparison.OrdinalIgnoreCase)))
                {
                    // ffmpeg sin fuentes (fontconfig) o sin drawtext: se sigue sin el texto encima.
                    _drawTextBroken = true;
                    quickFailures = Math.Max(0, quickFailures - 1);
                    _logger.LogWarning("ffmpeg no pudo dibujar texto sobre el video; los streams simulados seguirán sin el texto. Detalle: {Log}", Summarize(log));
                }
                else if (!basicSource && (log.Contains("No such filter", StringComparison.Ordinal) || log.Contains("Error initializing filter", StringComparison.Ordinal)))
                {
                    basicSource = true; // esta versión de ffmpeg no trae esa fuente: se usa testsrc2
                }

                runner.LastError = log.Length > 0 ? Summarize(log) : $"ffmpeg terminó (código {process.ExitCode}).";

                if (quickFailures >= 5)
                {
                    runner.Fail(Hint(runner.LastError));
                    _logger.LogWarning("El stream simulado del canal {ChannelId} falló 5 veces seguidas: {Error}", runner.ChannelId, runner.LastError);
                    break;
                }

                runner.State = "reconectando";
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, quickFailures)));
                if (!await DelayAsync(delay, ct)) break;
            }

            if (ct.IsCancellationRequested) runner.State = "detenido";
        }

        private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            try { await Task.Delay(delay, ct); return true; }
            catch (OperationCanceledException) { return false; }
        }

        private static string Summarize(string log)
        {
            var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var text = string.Join(" · ", lines.TakeLast(3));
            return text.Length > 400 ? text[..400] + "…" : text;
        }

        private static string Hint(string? error)
        {
            var e = error ?? string.Empty;
            if (e.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) || e.Contains("Cannot open connection", StringComparison.OrdinalIgnoreCase))
                return "MediaMTX no responde en el puerto RTMP. ¿Está corriendo? · " + e;
            if (e.Contains("401") || e.Contains("403") || e.Contains("authentication", StringComparison.OrdinalIgnoreCase) || e.Contains("Input/output error", StringComparison.OrdinalIgnoreCase))
                return "MediaMTX rechazó la publicación (clave o secreto de webhooks). · " + e;
            return e;
        }

        // ════════════════════════════════════════════════════════════════
        //  Argumentos de ffmpeg
        // ════════════════════════════════════════════════════════════════

        private (List<string> Args, string SourceName) BuildArguments(int channelId, ChannelStreamInfo info, string url, bool basicSource)
        {
            var (width, height) = ParseResolution(_settings.StreamResolution);
            var fps = Math.Clamp(_settings.StreamFps, 10, 60);
            var kbps = Math.Clamp(_settings.StreamBitrateKbps, 300, 8000);
            var gop = fps * 2; // fotograma clave cada 2 s (lo que pide Twitch y lo que necesita HLS)

            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats" };
            var filters = new List<string>();
            string sourceName;

            var video = PickVideoFile(channelId);
            if (video != null)
            {
                args.AddRange(new[] { "-re", "-stream_loop", "-1", "-i", video });
                args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?" });
                filters.Add($"scale={width}:{height}:force_original_aspect_ratio=decrease");
                filters.Add($"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2");
                filters.Add($"fps={fps}");
                sourceName = "video: " + Path.GetFileName(video);
            }
            else
            {
                var (videoSource, label) = basicSource
                    ? ($"testsrc2=size={width}x{height}:rate={fps}", "testsrc2")
                    : PickGeneratedSource(channelId, width, height, fps);
                var audioSource = PickAudioSource(channelId);
                args.AddRange(new[] { "-re", "-f", "lavfi", "-i", videoSource, "-f", "lavfi", "-i", audioSource });
                args.AddRange(new[] { "-map", "0:v:0", "-map", "1:a:0" });
                sourceName = "señal generada: " + label;
            }

            var overlay = BuildOverlay(channelId, info, height);
            if (overlay != null) filters.Add(overlay);
            filters.Add("format=yuv420p");

            args.AddRange(new[] { "-vf", string.Join(',', filters) });
            args.AddRange(new[]
            {
                "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-pix_fmt", "yuv420p",
                "-r", fps.ToString(CultureInfo.InvariantCulture),
                "-g", gop.ToString(CultureInfo.InvariantCulture), "-keyint_min", gop.ToString(CultureInfo.InvariantCulture), "-sc_threshold", "0",
                "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k",
                "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2",
                "-f", "flv", url
            });

            return (args, sourceName);
        }

        private static (int Width, int Height) ParseResolution(string? value)
        {
            var parts = (value ?? string.Empty).ToLowerInvariant().Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w is >= 160 and <= 3840 && h is >= 90 and <= 2160)
                return (w - w % 2, h - h % 2); // libx264 + yuv420p necesitan dimensiones pares
            return (1280, 720);
        }

        // Cada canal tiene "su" señal (siempre la misma), así se distinguen en el inicio.
        private (string Source, string Label) PickGeneratedSource(int channelId, int w, int h, int fps)
        {
            // Las fuentes pesadas se generan en baja resolución y se escalan.
            var options = new List<(string Filter, string Source)>
            {
                ("testsrc2", $"testsrc2=size={w}x{h}:rate={fps}"),
                ("mandelbrot", $"mandelbrot=size=640x360:rate={fps},scale={w}:{h}"),
                ("smptehdbars", $"smptehdbars=size={w}x{h}:rate={fps}"),
                ("life", $"life=size=320x180:rate={fps}:mold=10:ratio=0.1:life_color=0x9147ff:death_color=0x18181b,scale={w}:{h}:flags=neighbor"),
                ("gradients", $"gradients=size={w}x{h}:rate={fps}:speed=0.015"),
                ("cellauto", $"cellauto=size=320x180:rate={fps}:rule=110,scale={w}:{h}:flags=neighbor"),
                ("rgbtestsrc", $"rgbtestsrc=size={w}x{h}:rate={fps}"),
                ("sierpinski", $"sierpinski=size=640x360:rate={fps},scale={w}:{h}")
            };
            var available = options.Where(o => HasFilter(o.Filter)).ToList();
            if (available.Count == 0) return ($"testsrc2=size={w}x{h}:rate={fps}", "testsrc2");
            var pick = available[channelId % available.Count];
            return (pick.Source, pick.Filter);
        }

        private string PickAudioSource(int channelId)
        {
            // Acorde suave con vibrato (canales pares) o "lluvia" (ruido marrón, canales impares).
            if (channelId % 2 == 0 && HasFilter("aevalsrc"))
                return "aevalsrc=(0.05*sin(2*PI*220*t)+0.035*sin(2*PI*277.18*t)+0.03*sin(2*PI*329.63*t))*(0.7+0.3*sin(2*PI*0.2*t)):s=48000";
            if (HasFilter("anoisesrc"))
                return "anoisesrc=color=brown:amplitude=0.05:sample_rate=48000";
            return "sine=frequency=220:sample_rate=48000";
        }

        private string? PickVideoFile(int channelId)
        {
            var folder = _settings.VideosFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
            try
            {
                var files = Directory.EnumerateFiles(folder)
                    .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return files.Count == 0 ? null : files[channelId % files.Count];
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No se pudo leer la carpeta de videos {Folder}.", folder);
                return null;
            }
        }

        // Texto encima del video: nombre del canal, streamer, hora y tiempo al aire.
        // Se usa textfile= para no pelear con el escapado de ffmpeg (comillas, dos puntos...).
        private string? BuildOverlay(int channelId, ChannelStreamInfo info, int height)
        {
            if (_drawTextBroken || !HasFilter("drawtext")) return null;
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "twitchclone-sim");
                Directory.CreateDirectory(dir);
                var textFile = Path.Combine(dir, $"overlay-{channelId}.txt");
                var category = string.IsNullOrWhiteSpace(info.CategoryName) ? "Sin categoría" : info.CategoryName;
                var text = new StringBuilder()
                    .Append(EscapeDrawText(info.Name)).Append('\n')
                    .Append(EscapeDrawText($"{info.OwnerDisplayName} · {category}")).Append('\n')
                    .Append("STREAM SIMULADO · %{localtime:%X} · al aire %{pts:hms}")
                    .ToString();
                File.WriteAllText(textFile, text, new UTF8Encoding(false));

                var fontSize = Math.Max(14, height / 24);
                var font = FindFont();
                var options = new StringBuilder()
                    .Append("drawtext=textfile=").Append(FilterPath(textFile))
                    .Append(":fontcolor=white:fontsize=").Append(fontSize)
                    .Append(":box=1:boxcolor=black@0.55:boxborderw=").Append(fontSize / 2)
                    .Append(":line_spacing=").Append(fontSize / 3)
                    .Append(":x=").Append(fontSize).Append(":y=").Append(fontSize);
                if (font != null) options.Append(":fontfile=").Append(FilterPath(font));
                return options.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No se pudo preparar el texto del stream simulado.");
                return null;
            }
        }

        // En textfile la expansión de drawtext interpreta "\" y "%": se escapan.
        private static string EscapeDrawText(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%");

        // Ruta dentro de un filtro de ffmpeg: entre comillas simples y con ":" escapado (C\:/Windows/...).
        private static string FilterPath(string path)
        {
            if (path.Contains('\'')) throw new InvalidOperationException("Ruta con comilla simple: " + path);
            return "'" + path.Replace('\\', '/').Replace(":", "\\:") + "'";
        }

        private static string? FindFont()
        {
            string[] candidates =
            {
                @"C:\Windows\Fonts\segoeui.ttf", @"C:\Windows\Fonts\arial.ttf",
                "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/TTF/DejaVuSans.ttf",
                "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans.ttf", "/Library/Fonts/Arial.ttf",
                "/System/Library/Fonts/Supplemental/Arial.ttf"
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private sealed class StreamRunner
        {
            private readonly ConcurrentQueue<string> _log = new();

            public StreamRunner(int channelId) => ChannelId = channelId;

            public int ChannelId { get; }
            public CancellationTokenSource Cts { get; } = new();
            public Task Loop { get; set; } = Task.CompletedTask;
            public volatile string State = "iniciando";
            public volatile string Source = string.Empty;
            public DateTimeOffset? Since;
            public int Restarts;
            public volatile string? LastError;

            public void AddLog(string line)
            {
                _log.Enqueue(line);
                while (_log.Count > 30) _log.TryDequeue(out _);
            }

            public string TakeLog()
            {
                var sb = new StringBuilder();
                while (_log.TryDequeue(out var line)) sb.AppendLine(line);
                return sb.ToString();
            }

            public void Fail(string error)
            {
                LastError = error;
                State = "error";
                Since = null;
            }
        }
    }

    // Ejecuta ffmpeg y espera a que termine (para probar la instalación y sacar miniaturas).
    public static class FfmpegRunner
    {
        public sealed record Result(int ExitCode, string Output, string? Error);

        public static async Task<Result> RunAsync(string path, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct, bool captureStdout = false)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(path)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = captureStdout,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                }
            };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);

            try
            {
                process.Start();
                ChildProcessTracker.Track(process);
            }
            catch (Exception ex)
            {
                return new Result(-1, string.Empty, ex.Message);
            }

            var stdout = captureStdout ? process.StandardOutput.ReadToEndAsync(ct) : Task.FromResult(string.Empty);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
                return new Result(-1, string.Empty, ct.IsCancellationRequested ? "Cancelado." : $"ffmpeg tardó más de {timeout.TotalSeconds:0} s.");
            }

            string output = string.Empty, error = string.Empty;
            try { output = await stdout; error = await stderr; } catch { /* salida incompleta */ }
            return new Result(process.ExitCode, output, process.ExitCode == 0 ? null : (string.IsNullOrWhiteSpace(error) ? $"código {process.ExitCode}" : error.Trim()));
        }

        // "q" es la forma limpia de cerrar ffmpeg (cierra el FLV y MediaMTX ve la desconexión al tiro).
        public static async Task StopGracefullyAsync(Process process)
        {
            try
            {
                if (process.HasExited) return;
                await process.StandardInput.WriteAsync('q');
                await process.StandardInput.FlushAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(cts.Token);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
            }
        }
    }
}
