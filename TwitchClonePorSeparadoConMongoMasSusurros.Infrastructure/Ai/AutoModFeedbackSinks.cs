using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Ai
{
    // Fase 4: AutoMod sin servicio que aprenda (Ollama, solo reglas o SendFeedback = false).
    public sealed class NullAutoModFeedbackSink : IAutoModFeedbackSink
    {
        public bool IsEnabled => false;
        public void Enqueue(AutoModFeedback feedback) { }
    }

    // Fase 4: manda las decisiones de los moderadores al servicio Python (POST {AutoMod:FeedbackUrl}).
    //
    //   { "text": "...", "isToxic": true, "category": "insulto",
    //     "source": "automod_rechazado", "channelId": 12 }
    //
    // El chat nunca espera por esto: Enqueue deja el item en una cola en memoria y un
    // BackgroundService lo envía. Si el servicio está caído, se reintenta 2 veces y se
    // descarta (perder un ejemplo de entrenamiento no es grave; congelar el chat sí).
    public sealed class HttpAutoModFeedbackSink : BackgroundService, IAutoModFeedbackSink
    {
        private const int QueueCapacity = 1000;
        private static readonly TimeSpan[] DefaultRetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5) };

        private readonly Channel<AutoModFeedback> _queue = Channel.CreateBounded<AutoModFeedback>(
            new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private readonly HttpClient _http;
        private readonly AutoModSettings _settings;
        private readonly ILogger<HttpAutoModFeedbackSink> _logger;
        private readonly TimeSpan[] _retryDelays;

        // retryDelays: solo para las pruebas (para no esperar 6 s).
        public HttpAutoModFeedbackSink(HttpClient http, IOptions<AutoModSettings> settings, ILogger<HttpAutoModFeedbackSink> logger,
            TimeSpan[]? retryDelays = null)
        {
            _http = http;
            _settings = settings.Value;
            _logger = logger;
            _retryDelays = retryDelays ?? DefaultRetryDelays;
        }

        public bool IsEnabled => !string.IsNullOrWhiteSpace(_settings.FeedbackUrl);

        // Para las pruebas y el panel: cuántos se enviaron bien y cuántos se perdieron.
        public int Sent => _sent;
        public int Dropped => _dropped;
        private int _sent;
        private int _dropped;

        public void Enqueue(AutoModFeedback feedback)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(feedback.Text)) return;
            _queue.Writer.TryWrite(feedback); // con DropOldest siempre acepta
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
                    await SendWithRetriesAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // La app se está cerrando.
            }
        }

        private async Task SendWithRetriesAsync(AutoModFeedback item, CancellationToken ct)
        {
            var body = new
            {
                text = item.Text.Length > 2000 ? item.Text[..2000] : item.Text,
                isToxic = item.IsToxic,
                category = item.Category,
                source = item.Source,
                channelId = item.ChannelId
            };

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var response = await _http.PostAsJsonAsync(_settings.FeedbackUrl, body, ct);
                    response.EnsureSuccessStatusCode();
                    Interlocked.Increment(ref _sent);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    if (attempt >= _retryDelays.Length)
                    {
                        Interlocked.Increment(ref _dropped);
                        _logger.LogDebug(ex, "No se pudo enviar el feedback de AutoMod a {Url}; se descarta.", _settings.FeedbackUrl);
                        return;
                    }
                    await Task.Delay(_retryDelays[attempt], ct);
                }
            }
        }
    }
}
