using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes
{
    // Reemplazan a NotificationService, LiveMetricService y StreamSessionService (MongoDB).

    public sealed class InMemoryNotificationService : INotificationService
    {
        private readonly InMemoryPlatform _db;

        public InMemoryNotificationService(InMemoryPlatform db) => _db = db;

        public Task NotifyFollowersAsync(List<string> followerIds, string channelId, string channelName)
        {
            _db.LiveNotifications.Enqueue(new InMemoryPlatform.LiveNotification(followerIds.ToList(), channelId, channelName));
            return Task.CompletedTask;
        }

        public Task CreateNotificationAsync(string userId, string message, string type, string? channelName, string? channelId) => Task.CompletedTask;
        public Task<List<Notification>> GetUserNotificationsAsync(string userId) => Task.FromResult(new List<Notification>());
        public Task<List<Notification>> GetUnreadAsync(string userId) => Task.FromResult(new List<Notification>());
        public Task MarkAsReadAsync(string notificationId) => Task.CompletedTask;
        public Task MarkAllAsReadAsync(string userId) => Task.CompletedTask;
        public Task<long> GetUnreadCountAsync(string userId) => Task.FromResult(0L);
        public Task DeleteNotificationAsync(string notificationId) => Task.CompletedTask;
    }

    public sealed class InMemoryLiveMetricService : ILiveMetricService
    {
        private readonly InMemoryPlatform _db;

        public InMemoryLiveMetricService(InMemoryPlatform db) => _db = db;

        public Task StartStreamAsync(string channelId)
        {
            ThrowIfDown();
            _db.Metrics[channelId] = new LiveMetric { ChannelId = channelId, IsLive = true };
            return Task.CompletedTask;
        }

        public Task StopStreamAsync(string channelId)
        {
            ThrowIfDown();
            if (_db.Metrics.TryGetValue(channelId, out var m)) { m.IsLive = false; m.ViewerCount = 0; }
            return Task.CompletedTask;
        }

        public Task SetViewerCountAsync(string channelId, int viewers)
        {
            ThrowIfDown();
            var m = _db.Metrics.GetOrAdd(channelId, id => new LiveMetric { ChannelId = id, IsLive = true });
            m.ViewerCount = viewers;
            m.PeakViewers = Math.Max(m.PeakViewers, viewers);
            return Task.CompletedTask;
        }

        public Task<LiveMetric?> GetMetricAsync(string channelId)
        {
            ThrowIfDown();
            return Task.FromResult(_db.Metrics.TryGetValue(channelId, out var m) ? m : null);
        }

        public Task<List<LiveMetric>> GetLiveChannelsAsync() =>
            Task.FromResult(_db.Metrics.Values.Where(m => m.IsLive).ToList());

        public Task IncrementViewersAsync(string channelId) => Task.CompletedTask;
        public Task DecrementViewersAsync(string channelId) => Task.CompletedTask;

        private void ThrowIfDown()
        {
            if (_db.FailLiveMetrics) throw new InvalidOperationException("MongoDB no disponible (simulado).");
        }
    }

    public sealed class InMemoryStreamSessionService : IStreamSessionService
    {
        private readonly InMemoryPlatform _db;

        public InMemoryStreamSessionService(InMemoryPlatform db) => _db = db;

        public Task<StreamSession> StartSessionAsync(string channelId, string channelName, string title, string categoryName)
        {
            var session = new StreamSession
            {
                ChannelId = channelId,
                ChannelName = channelName,
                Title = title,
                CategoryName = categoryName,
                StartedAt = _db.Clock.GetUtcNow().UtcDateTime
            };
            lock (_db.Sessions) _db.Sessions.Add(session);
            return Task.FromResult(session);
        }

        public Task EndSessionAsync(string channelId, int peakViewers, int avgViewers)
        {
            lock (_db.Sessions)
            {
                var session = _db.Sessions.LastOrDefault(s => s.ChannelId == channelId && s.EndedAt == null);
                if (session != null)
                {
                    session.EndedAt = _db.Clock.GetUtcNow().UtcDateTime;
                    session.PeakViewers = peakViewers;
                    session.AvgViewers = avgViewers;
                    session.DurationSeconds = (long)(session.EndedAt.Value - session.StartedAt).TotalSeconds;
                }
            }
            return Task.CompletedTask;
        }

        public Task<StreamSession?> GetActiveSessionAsync(string channelId)
        {
            lock (_db.Sessions) return Task.FromResult(_db.Sessions.LastOrDefault(s => s.ChannelId == channelId && s.EndedAt == null));
        }

        public Task<List<StreamSession>> GetChannelHistoryAsync(string channelId, int limit = 20)
        {
            lock (_db.Sessions)
                return Task.FromResult(_db.Sessions.Where(s => s.ChannelId == channelId).OrderByDescending(s => s.StartedAt).Take(limit).ToList());
        }
    }
}
