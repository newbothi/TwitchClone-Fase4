using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    public interface IFollowService
    {
        Task FollowAsync(string followerId, string followedId);
        Task UnfollowAsync(string followerId, string followedId);
        Task<bool> IsFollowingAsync(string followerId, string followedId);
        Task<IEnumerable<ChannelDto>> GetFollowedLiveChannelsAsync(string userId);
        Task<int> GetFollowerCountAsync(string userId);
        Task<int> GetFollowingCountAsync(string userId);
        Task<IEnumerable<AppUser>> GetFollowersAsync(string userId);
        Task<IEnumerable<AppUser>> GetFollowingAsync(string userId);
    }
}