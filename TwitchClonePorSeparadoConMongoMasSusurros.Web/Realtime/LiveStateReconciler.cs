using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime
{
    // Red de seguridad para los webhooks: cada N segundos compara qué canales están
    // EN VIVO en SQL con lo que MediaMTX realmente está transmitiendo, y corrige.
    // Casos que arregla: la app estaba apagada cuando OBS se detuvo, un webhook falló,
    // o canales marcados "en vivo" sin stream (como los canales demo antiguos).
    //
    // Si la API de MediaMTX no responde (MediaMTX apagado), no toca nada.
    public sealed class LiveStateReconciler : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IHubContext<StreamHub> _hub;
        private readonly IOptionsMonitor<MediaServerSettings> _settings;
        private readonly ILogger<LiveStateReconciler> _logger;
        private bool _apiWasReachable = true;

        public LiveStateReconciler(
            IServiceScopeFactory scopes,
            IHubContext<StreamHub> hub,
            IOptionsMonitor<MediaServerSettings> settings,
            ILogger<LiveStateReconciler> logger)
        {
            _scopes = scopes;
            _hub = hub;
            _settings = settings;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Darle tiempo a MediaMTX y a la app a terminar de arrancar.
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = _settings.CurrentValue;
                if (settings.ReconcileLiveState)
                {
                    try
                    {
                        await ReconcileOnceAsync(settings, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Error al sincronizar el estado EN VIVO con MediaMTX.");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, settings.ReconcileIntervalSeconds)), stoppingToken);
            }
        }

        private async Task ReconcileOnceAsync(MediaServerSettings settings, CancellationToken ct)
        {
            using var scope = _scopes.CreateScope();
            var media = scope.ServiceProvider.GetRequiredService<IMediaServerClient>();

            var paths = await media.GetAvailablePathsAsync(ct);
            if (paths == null)
            {
                if (_apiWasReachable)
                    _logger.LogInformation("La API de MediaMTX ({ApiBaseUrl}) no responde; se omite la sincronización del estado EN VIVO.", settings.ApiBaseUrl);
                _apiWasReachable = false;
                return;
            }
            _apiWasReachable = true;

            var publishing = new HashSet<int>();
            foreach (var path in paths)
                if (settings.TryParseChannelId(path, out var id)) publishing.Add(id);

            var channels = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
            var lifecycle = scope.ServiceProvider.GetRequiredService<IStreamLifecycleService>();
            var liveInDb = (await channels.GetLiveChannelIdsAsync()).ToHashSet();

            foreach (var channelId in liveInDb.Except(publishing))
            {
                var change = await lifecycle.MarkOfflineAsync(channelId);
                if (change?.Changed == true)
                {
                    _logger.LogInformation("Sincronización: canal {ChannelId} estaba EN VIVO sin stream en MediaMTX → OFFLINE.", channelId);
                    await _hub.Clients.Group(StreamHub.ChannelGroup(channelId)).SendAsync("StreamEnded", ct);
                }
            }

            foreach (var channelId in publishing.Except(liveInDb))
            {
                var change = await lifecycle.MarkLiveAsync(channelId);
                if (change?.Changed == true)
                {
                    _logger.LogInformation("Sincronización: canal {ChannelId} transmitiendo en MediaMTX pero OFFLINE en la base → EN VIVO.", channelId);
                    await _hub.Clients.Group(StreamHub.ChannelGroup(channelId)).SendAsync("StreamStarted", ct);
                }
            }
        }
    }
}
