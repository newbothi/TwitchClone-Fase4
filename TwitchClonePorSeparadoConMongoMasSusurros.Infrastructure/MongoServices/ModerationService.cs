using Microsoft.EntityFrameworkCore;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    // Bans y timeouts por canal sobre la tabla BannedUsers (SQL Server).
    // (Vive en la carpeta MongoServices por historia del proyecto, pero usa EF Core.)
    //
    // Fechas: ExpiresAt y CreatedAt se guardan en UTC para que la comparación
    // con "ahora" no dependa de la zona horaria del servidor.
    public class ModerationService : IModerationService
    {
        private readonly AppDbContext _db;

        public ModerationService(AppDbContext db)
        {
            _db = db;
        }

        public async Task<bool> BanAsync(int channelId, string userId, string bannedById, string reason)
            => await UpsertAsync(channelId, userId, bannedById, reason, expiresAtUtc: null);

        public async Task<bool> TimeoutAsync(int channelId, string userId, string bannedById, TimeSpan duration, string? reason = null)
            => await UpsertAsync(channelId, userId, bannedById,
                reason ?? $"Timeout de {(int)duration.TotalSeconds} s",
                DateTime.UtcNow.Add(duration));

        public async Task<bool> UnbanAsync(int channelId, string userId)
        {
            var ban = await _db.BannedUsers
                .FirstOrDefaultAsync(b => b.ChannelId == channelId && b.UserId == userId);

            if (ban == null) return false;

            _db.BannedUsers.Remove(ban);
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<BannedUser?> GetActiveBanAsync(int channelId, string userId)
        {
            var ban = await _db.BannedUsers
                .FirstOrDefaultAsync(b => b.ChannelId == channelId && b.UserId == userId);

            if (ban == null) return null;

            // Timeout vencido: se limpia solo.
            if (ban.ExpiresAt.HasValue && ban.ExpiresAt.Value <= DateTime.UtcNow)
            {
                _db.BannedUsers.Remove(ban);
                await _db.SaveChangesAsync();
                return null;
            }

            return ban;
        }

        public async Task<bool> IsBannedAsync(int channelId, string userId)
            => await GetActiveBanAsync(channelId, userId) != null;

        public async Task<List<BannedUser>> GetBannedUsersAsync(int channelId)
        {
            return await _db.BannedUsers
                .Include(b => b.User)
                .Include(b => b.BannedBy)
                .Where(b => b.ChannelId == channelId)
                .OrderByDescending(b => b.CreatedAt)
                .ToListAsync();
        }

        private async Task<bool> UpsertAsync(int channelId, string userId, string bannedById, string reason, DateTime? expiresAtUtc)
        {
            // Nunca se puede sancionar al dueño del canal.
            var ownerId = await _db.Channels
                .Where(c => c.Id == channelId)
                .Select(c => c.UserId)
                .FirstOrDefaultAsync();

            if (ownerId == null || ownerId == userId) return false;

            var existing = await _db.BannedUsers
                .FirstOrDefaultAsync(b => b.ChannelId == channelId && b.UserId == userId);

            if (existing != null)
            {
                existing.ExpiresAt = expiresAtUtc;
                existing.Reason = reason;
                existing.BannedById = bannedById;
                existing.CreatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.BannedUsers.Add(new BannedUser
                {
                    ChannelId = channelId,
                    UserId = userId,
                    BannedById = bannedById,
                    Reason = reason,
                    ExpiresAt = expiresAtUtc,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync();
            return true;
        }
    }
}
