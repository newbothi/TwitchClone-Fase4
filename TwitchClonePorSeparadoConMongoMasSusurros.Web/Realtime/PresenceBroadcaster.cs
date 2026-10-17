using Microsoft.AspNetCore.SignalR;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime
{
    // Cada 2 s difunde el conteo de viewers de los canales que cambiaron.
    // Cada 15 s guarda el conteo real en SQL (Channels.ViewerCount, que usa la portada
    // para ordenar) y en Mongo (LiveMetrics: actual + pico).
    // Antes ViewerCount nunca se actualizaba y el pico quedaba siempre en 0.
    public sealed class PresenceBroadcaster : BackgroundService
    {
        private static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(15);

        private readonly PresenceTracker _presence;
        private readonly IHubContext<StreamHub> _hub;
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<PresenceBroadcaster> _logger;

        private Dictionary<int, int> _lastPersisted = new();

        public PresenceBroadcaster(
            PresenceTracker presence,
            IHubContext<StreamHub> hub,
            IServiceScopeFactory scopes,
            ILogger<PresenceBroadcaster> logger)
        {
            _presence = presence;
            _hub = hub;
            _scopes = scopes;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(BroadcastInterval);
            var sinceLastPersist = TimeSpan.Zero;

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var channelId in _presence.DrainDirty())
                    {
                        await _hub.Clients.Group(StreamHub.ChannelGroup(channelId))
                            .SendAsync("ViewerCountUpdated", _presence.GetCount(channelId), stoppingToken);
                    }

                    sinceLastPersist += BroadcastInterval;
                    if (sinceLastPersist >= PersistInterval)
                    {
                        sinceLastPersist = TimeSpan.Zero;
                        await PersistAsync();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Error al difundir/guardar el conteo de viewers.");
                }
            }
        }

        private async Task PersistAsync()
        {
            var current = _presence.SnapshotCounts();

            // Se escriben todos los canales con viewers, y en 0 los que tenían y ya no.
            var toWrite = new Dictionary<int, int>(current);
            foreach (var channelId in _lastPersisted.Keys)
                if (!current.ContainsKey(channelId)) toWrite[channelId] = 0;

            _lastPersisted = current;
            if (toWrite.Count == 0) return;

            using var scope = _scopes.CreateScope();
            var channels = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
            await channels.UpdateViewerCountsAsync(toWrite);

            var metrics = scope.ServiceProvider.GetRequiredService<ILiveMetricService>();
            foreach (var (channelId, count) in toWrite)
            {
                try
                {
                    await metrics.SetViewerCountAsync(channelId.ToString(), count);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "No se pudo actualizar LiveMetrics del canal {ChannelId}.", channelId);
                    break; // Mongo caído: no insistir con el resto en este ciclo
                }
            }
        }
    }
}
