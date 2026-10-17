using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes
{
    public sealed class InMemoryFollowService : IFollowService
    {
        private readonly InMemoryPlatform _db;

        public InMemoryFollowService(InMemoryPlatform db) => _db = db;

        public Task FollowAsync(string followerId, string followedId)
        {
            _db.Follow(followerId, followedId);
            return Task.CompletedTask;
        }

        public Task UnfollowAsync(string followerId, string followedId)
        {
            _db.Follows.TryRemove((followerId, followedId), out _);
            return Task.CompletedTask;
        }

        public Task<bool> IsFollowingAsync(string followerId, string followedId) =>
            Task.FromResult(_db.Follows.ContainsKey((followerId, followedId)));

        public Task<int> GetFollowerCountAsync(string userId) =>
            Task.FromResult(_db.Follows.Keys.Count(k => k.FollowedId == userId));

        public Task<int> GetFollowingCountAsync(string userId) =>
            Task.FromResult(_db.Follows.Keys.Count(k => k.FollowerId == userId));

        public Task<IEnumerable<AppUser>> GetFollowersAsync(string userId) =>
            Task.FromResult<IEnumerable<AppUser>>(_db.Follows.Keys
                .Where(k => k.FollowedId == userId)
                .Select(k => _db.Users[k.FollowerId])
                .ToList());

        public Task<IEnumerable<AppUser>> GetFollowingAsync(string userId) =>
            Task.FromResult<IEnumerable<AppUser>>(_db.Follows.Keys
                .Where(k => k.FollowerId == userId)
                .Select(k => _db.Users[k.FollowedId])
                .ToList());

        public Task<IEnumerable<ChannelDto>> GetFollowedLiveChannelsAsync(string userId) =>
            Task.FromResult<IEnumerable<ChannelDto>>(_db.Channels.Values
                .Where(c => c.IsLive && _db.Follows.ContainsKey((userId, c.UserId)))
                .Select(c => new ChannelDto { Id = c.Id, UserId = c.UserId, Name = c.Name, IsLive = true, DisplayUsername = c.OwnerDisplayName })
                .ToList());
    }
}
