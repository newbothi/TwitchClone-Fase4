using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    // Consultas livianas (Dapper) sobre usuarios que necesita el chat.
    public interface IChatUserRepository
    {
        // Para comandos como "/ban ViewerUno" (acepta "@ViewerUno").
        Task<ChatUserLookup?> FindByDisplayNameAsync(string displayName);

        Task<DateTime?> GetFollowDateAsync(string followerId, string followedId);

        // Puntos del canal (Users.Points).
        Task<int> GetPointsAsync(string userId);
        Task AddPointsAsync(IReadOnlyCollection<string> userIds, int amount);

        // Descuenta los puntos y registra la donación en una sola transacción.
        // false si no le alcanzan los puntos (nunca deja el saldo negativo).
        Task<bool> TryDonatePointsAsync(string fromUserId, int channelId, int amount, string message);
    }
}
