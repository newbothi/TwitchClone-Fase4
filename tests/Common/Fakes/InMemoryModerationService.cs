using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes
{
    // Reemplaza a ModerationService (tabla BannedUsers). Usa el reloj de la prueba
    // para decidir si un timeout ya venció.
    public sealed class InMemoryModerationService : IModerationService
    {
        private readonly InMemoryPlatform _db;

        public InMemoryModerationService(InMemoryPlatform db) => _db = db;

        private DateTime Now => _db.Clock.GetUtcNow().UtcDateTime;

        public Task<bool> BanAsync(int channelId, string userId, string bannedById, string reason) =>
            Upsert(channelId, userId, bannedById, reason, expiresAt: null);

        public Task<bool> TimeoutAsync(int channelId, string userId, string bannedById, TimeSpan duration, string? reason = null) =>
            Upsert(channelId, userId, bannedById, reason ?? "Timeout", Now.Add(duration));

        public Task<bool> UnbanAsync(int channelId, string userId) =>
            Task.FromResult(_db.Bans.TryRemove((channelId, userId), out _));

        public Task<BannedUser?> GetActiveBanAsync(int channelId, string userId)
        {
            if (!_db.Bans.TryGetValue((channelId, userId), out var ban)) return Task.FromResult<BannedUser?>(null);
            if (ban.ExpiresAt.HasValue && ban.ExpiresAt.Value <= Now)
            {
                _db.Bans.TryRemove((channelId, userId), out _);
                return Task.FromResult<BannedUser?>(null);
            }
            return Task.FromResult<BannedUser?>(ban);
        }

        public async Task<bool> IsBannedAsync(int channelId, string userId) =>
            await GetActiveBanAsync(channelId, userId) != null;

        public Task<List<BannedUser>> GetBannedUsersAsync(int channelId) =>
            Task.FromResult(_db.Bans.Values.Where(b => b.ChannelId == channelId).ToList());

        private Task<bool> Upsert(int channelId, string userId, string bannedById, string reason, DateTime? expiresAt)
        {
            // Igual que el servicio real: el dueño del canal no se puede sancionar.
            if (_db.Channels.TryGetValue(channelId, out var channel) && channel.UserId == userId)
                return Task.FromResult(false);

            _db.Bans[(channelId, userId)] = new BannedUser
            {
                ChannelId = channelId,
                UserId = userId,
                BannedById = bannedById,
                Reason = reason,
                ExpiresAt = expiresAt,
                CreatedAt = Now
            };
            return Task.FromResult(true);
        }
    }
}
