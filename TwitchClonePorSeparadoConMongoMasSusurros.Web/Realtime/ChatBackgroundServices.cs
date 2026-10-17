using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime
{
    // Difunde el estado de las encuestas como mucho una vez por segundo y las cierra al vencer.
    // (Con cientos de bots votando, difundir cada voto saturaría a los navegadores.)
    public sealed class PollTicker : BackgroundService
    {
        private readonly IPollService _polls;
        private readonly IHubContext<StreamHub> _hub;
        private readonly ILogger<PollTicker> _logger;

        public PollTicker(IPollService polls, IHubContext<StreamHub> hub, ILogger<PollTicker> logger)
        {
            _polls = polls;
            _hub = hub;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var poll in _polls.DrainChanged())
                        await _hub.Clients.Group(StreamHub.ChannelGroup(poll.ChannelId)).SendAsync("PollUpdated", poll, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Error al difundir encuestas.");
                }
            }
        }
    }

    // Puntos del canal: cada N minutos, todos los usuarios con sesión que estén viendo
    // un canal EN VIVO ganan puntos (Users.Points). Se usan para donar y, más adelante,
    // para predicciones y recompensas.
    public sealed class PointsAccrualService : BackgroundService
    {
        private readonly PresenceTracker _presence;
        private readonly IServiceScopeFactory _scopes;
        private readonly IHubContext<StreamHub> _hub;
        private readonly IOptionsMonitor<PointsSettings> _settings;
        private readonly ILogger<PointsAccrualService> _logger;

        public PointsAccrualService(PresenceTracker presence, IServiceScopeFactory scopes, IHubContext<StreamHub> hub,
            IOptionsMonitor<PointsSettings> settings, ILogger<PointsAccrualService> logger)
        {
            _presence = presence;
            _scopes = scopes;
            _hub = hub;
            _settings = settings;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = _settings.CurrentValue;
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, settings.IntervalMinutes)), stoppingToken);

                try
                {
                    await AccrueOnceAsync(settings.AmountPerInterval);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "No se pudieron repartir los puntos del canal.");
                }
            }
        }

        public async Task<int> AccrueOnceAsync(int amount)
        {
            using var scope = _scopes.CreateScope();
            var channels = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
            var users = scope.ServiceProvider.GetRequiredService<IChatUserRepository>();

            var live = (await channels.GetLiveChannelIdsAsync()).ToHashSet();
            var viewers = _presence.SnapshotCounts().Keys
                .Where(live.Contains)
                .SelectMany(_presence.GetUserIds)
                .Distinct()
                .ToList();

            if (viewers.Count == 0 || amount <= 0) return 0;

            await users.AddPointsAsync(viewers, amount);
            foreach (var chunk in viewers.Chunk(500))
                await _hub.Clients.Users(chunk).SendAsync("PointsEarned", amount);

            return viewers.Count;
        }
    }
}
