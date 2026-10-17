using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services
{
    public class FollowService : IFollowService
    {
        private readonly IFollowRepository _followRepository;
        private readonly IUserRepository _userRepository;
        private readonly IChannelRepository _channelRepository;

        public FollowService(
            IFollowRepository followRepository,
            IUserRepository userRepository,
            IChannelRepository channelRepository)
        {
            _followRepository = followRepository;
            _userRepository = userRepository;
            _channelRepository = channelRepository;
        }

        public async Task FollowAsync(string followerId, string followedId)
        {
            // No seguirse a uno mismo
            if (followerId == followedId) return;

            var alreadyFollowing = await _followRepository
                .IsFollowingAsync(followerId, followedId);

            if (!alreadyFollowing)
                await _followRepository.FollowAsync(followerId, followedId);
        }

        public async Task UnfollowAsync(string followerId, string followedId)
        {
            var isFollowing = await _followRepository
                .IsFollowingAsync(followerId, followedId);

            if (isFollowing)
                await _followRepository.UnfollowAsync(followerId, followedId);
        }

        public async Task<bool> IsFollowingAsync(string followerId, string followedId)
        {
            return await _followRepository.IsFollowingAsync(followerId, followedId);
        }

        public async Task<IEnumerable<ChannelDto>> GetFollowedLiveChannelsAsync(string userId)
        {
            var following = await _followRepository.GetFollowingAsync(userId);
            var liveChannels = new List<ChannelDto>();

            foreach (var followedUser in following)
            {
                var channel = await _channelRepository.GetByUserIdAsync(followedUser.Id);
                if (channel != null && channel.IsLive)
                {
                    liveChannels.Add(new ChannelDto
                    {
                        Id = channel.Id,
                        UserId = channel.UserId,
                        DisplayUsername = followedUser.DisplayUsername,
                        Name = channel.Name,
                        Description = channel.Description,
                        IsLive = channel.IsLive,
                        ViewerCount = channel.ViewerCount
                    });
                }
            }

            return liveChannels;
        }

        public async Task<int> GetFollowerCountAsync(string userId)
        {
            return await _followRepository.GetFollowerCountAsync(userId);
        }

        public async Task<int> GetFollowingCountAsync(string userId)
        {
            return await _followRepository.GetFollowingCountAsync(userId);
        }

        public async Task<IEnumerable<AppUser>> GetFollowersAsync(string userId)
        {
            return await _followRepository.GetFollowersAsync(userId);
        }

        public async Task<IEnumerable<AppUser>> GetFollowingAsync(string userId)
        {
            return await _followRepository.GetFollowingAsync(userId);
        }
    }
}