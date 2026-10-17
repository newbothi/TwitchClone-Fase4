using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    public interface IChannelRepository
    {
        Task<Channel?> GetByUserIdAsync(string userId);
        Task<Channel?> GetByIdAsync(int channelId);
        Task<IEnumerable<Channel>> GetLiveChannelsAsync();
        Task<IEnumerable<Channel>> GetByategoryAsync(int categoryId);
        Task CreateAsync(Channel channel);
        Task UpdateAsync(Channel channel);

        // ── Consultas livianas (Dapper) para el chat y el streaming ──────────

        Task<string?> GetOwnerUserIdAsync(int channelId);
        Task<IEnumerable<Emote>> GetEmotesAsync(int channelId);

        // Datos mínimos para validar una publicación desde OBS.
        Task<ChannelStreamInfo?> GetStreamInfoAsync(int channelId);

        // Cambia IsLive (y deja ViewerCount en 0). Devuelve true si el estado cambió.
        Task<bool> SetLiveStateAsync(int channelId, bool isLive);

        // Actualiza ViewerCount solo de canales que siguen EN VIVO.
        Task UpdateViewerCountsAsync(IReadOnlyDictionary<int, int> viewerCountsByChannel);

        Task<IEnumerable<int>> GetLiveChannelIdsAsync();

        Task UpdateStreamKeyAsync(int channelId, string streamKey);

        // Todos los canales con dueño y categoría (panel del simulador).
        Task<IEnumerable<ChannelSummary>> GetChannelSummariesAsync();
    }
}
