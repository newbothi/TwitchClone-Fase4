using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    public interface IChannelService
    {
        Task<ChannelDto?> GetByUserIdAsync(string userId);
        Task<IEnumerable<ChannelDto>> GetLiveChannelsAsync();
        Task<IEnumerable<ChannelDto>> GetByCategoryAsync(int categoryId);
        Task CreateChannelAsync(string userId, string displayUsername, int? categoryId = null);

        // Edita SOLO el canal del usuario indicado (ya no recibe un channelId del formulario).
        Task<bool> UpdateChannelAsync(string userId, string title, string description);

        Task<string?> GetStreamKeyAsync(string userId);

        // Genera una clave nueva y devuelve el Id del canal (null si el usuario no tiene canal).
        Task<int?> RegenerateStreamKeyAsync(string userId);
    }
}
