using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    // Bans y timeouts por canal (tabla BannedUsers en SQL Server).
    public interface IModerationService
    {
        // Devuelven false si el objetivo es el dueño del canal (no se puede banear a sí mismo).
        Task<bool> BanAsync(int channelId, string userId, string bannedById, string reason);
        Task<bool> TimeoutAsync(int channelId, string userId, string bannedById, TimeSpan duration, string? reason = null);
        Task<bool> UnbanAsync(int channelId, string userId);

        // Ban o timeout vigente, o null si puede chatear. Limpia timeouts vencidos.
        Task<BannedUser?> GetActiveBanAsync(int channelId, string userId);
        Task<bool> IsBannedAsync(int channelId, string userId);

        Task<List<BannedUser>> GetBannedUsersAsync(int channelId);
    }
}
