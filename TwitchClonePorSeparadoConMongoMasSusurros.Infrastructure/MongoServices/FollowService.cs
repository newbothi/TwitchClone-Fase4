using Microsoft.EntityFrameworkCore;

//using TwitchCloneMasMongo.Data;
//using TwitchCloneMasMongo.Models;

using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
//using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public class FollowService
    {
        private readonly AppDbContext _db;
        private readonly NotificationService _notification;

        public FollowService(AppDbContext db, NotificationService notification)
        {
            _db = db;
            _notification = notification;
        }

        public async Task<bool> FollowAsync(string followerId, string followedId)
        {
            if (followerId == followedId) return false;

            var exists = await _db.Follows
                .AnyAsync(f => f.FollowerId == followerId && f.FollowedId == followedId);
            if (exists) return false;

            _db.Follows.Add(new Follow
            {
                FollowerId = followerId,
                FollowedId = followedId,
                CreatedAt = DateTime.Now
            });
            await _db.SaveChangesAsync();

            var followedChannel = await _db.Channels
                .FirstOrDefaultAsync(c => c.UserId == followedId && c.IsLive);

            if (followedChannel != null)
            {
                await _notification.NotifyFollowersAsync(
                    new List<string> { followerId },
                    followedChannel.Id.ToString(),
                    followedChannel.Name);
            }

            return true;
        }

        public async Task<bool> UnfollowAsync(string followerId, string followedId)
        {
            var follow = await _db.Follows
                .FirstOrDefaultAsync(f => f.FollowerId == followerId && f.FollowedId == followedId);
            if (follow == null) return false;
            _db.Follows.Remove(follow);
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> IsFollowingAsync(string followerId, string followedId)
        {
            return await _db.Follows
                .AnyAsync(f => f.FollowerId == followerId && f.FollowedId == followedId);
        }

        public async Task<List<AppUser>> GetFollowersAsync(string userId)
        {
            return await _db.Follows
                .Where(f => f.FollowedId == userId)
                .Include(f => f.Follower)
                .Select(f => f.Follower!)
                .ToListAsync();
        }

        public async Task<List<AppUser>> GetFollowingAsync(string userId)
        {
            return await _db.Follows
                .Where(f => f.FollowerId == userId)
                .Include(f => f.Followed)
                .Select(f => f.Followed!)
                .ToListAsync();
        }

        public async Task<int> GetFollowerCountAsync(string userId)
        {
            return await _db.Follows
                .CountAsync(f => f.FollowedId == userId);
        }

        public async Task<int> GetFollowingCountAsync(string userId)
        {
            return await _db.Follows
                .CountAsync(f => f.FollowerId == userId);
        }
    }
}