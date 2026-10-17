using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services
{
    public class ChannelService : IChannelService
    {
        private readonly IChannelRepository _channelRepository;
        private readonly IUserRepository _userRepository;

        public ChannelService(
            IChannelRepository channelRepository,
            IUserRepository userRepository)
        {
            _channelRepository = channelRepository;
            _userRepository = userRepository;
        }

        public async Task<ChannelDto?> GetByUserIdAsync(string userId)
        {
            var channel = await _channelRepository.GetByUserIdAsync(userId);
            if (channel == null) return null;

            var user = await _userRepository.GetByIdAsync(userId);

            return MapToDto(channel, user);
        }

        public async Task<IEnumerable<ChannelDto>> GetLiveChannelsAsync()
        {
            var channels = await _channelRepository.GetLiveChannelsAsync();
            var result = new List<ChannelDto>();

            foreach (var channel in channels)
            {
                var user = await _userRepository.GetByIdAsync(channel.UserId);
                result.Add(MapToDto(channel, user));
            }

            return result;
        }

        public async Task<IEnumerable<ChannelDto>> GetByCategoryAsync(int categoryId)
        {
            var channels = await _channelRepository.GetByategoryAsync(categoryId);
            var result = new List<ChannelDto>();

            foreach (var channel in channels)
            {
                var user = await _userRepository.GetByIdAsync(channel.UserId);
                result.Add(MapToDto(channel, user));
            }

            return result;
        }

        public async Task CreateChannelAsync(string userId, string displayUsername, int? categoryId = null)
        {
            var existing = await _channelRepository.GetByUserIdAsync(userId);
            if (existing != null) return; // ya tiene canal

            var channel = new Channel
            {
                UserId = userId,
                Name = displayUsername,
                Description = string.Empty,
                StreamKey = StreamKeyGenerator.NewKey(),
                IsLive = false,
                ViewerCount = 0,
                CategoryId = categoryId,
                CreatedAt = DateTime.Now
            };

            await _channelRepository.CreateAsync(channel);
        }

        // Antes recibía el channelId del formulario y cualquier streamer podía editar
        // cualquier canal cambiando ese número. Ahora solo edita el canal del usuario logueado.
        public async Task<bool> UpdateChannelAsync(string userId, string title, string description)
        {
            var channel = await _channelRepository.GetByUserIdAsync(userId);
            if (channel == null) return false;

            title = (title ?? string.Empty).Trim();
            if (title.Length == 0) return false;
            if (title.Length > 100) title = title[..100];

            description = (description ?? string.Empty).Trim();
            if (description.Length > 500) description = description[..500];

            channel.Name = title;
            channel.Description = description;

            await _channelRepository.UpdateAsync(channel);
            return true;
        }

        public async Task<string?> GetStreamKeyAsync(string userId)
        {
            var channel = await _channelRepository.GetByUserIdAsync(userId);
            return channel?.StreamKey;
        }

        public async Task<int?> RegenerateStreamKeyAsync(string userId)
        {
            var channel = await _channelRepository.GetByUserIdAsync(userId);
            if (channel == null) return null;

            await _channelRepository.UpdateStreamKeyAsync(channel.Id, StreamKeyGenerator.NewKey());
            return channel.Id;
        }

        // ── Mapper privado ─────────────────────────────────────────
        private static ChannelDto MapToDto(Channel channel, AppUser? user)
        {
            return new ChannelDto
            {
                Id = channel.Id,
                UserId = channel.UserId,
                UserName = user?.UserName ?? string.Empty,
                DisplayUsername = user?.DisplayUsername ?? string.Empty,
                Name = channel.Name,
                Description = channel.Description,
                IsLive = channel.IsLive,
                ViewerCount = channel.ViewerCount,
                CategoryName = channel.Category?.Name
            };
        }
    }
}