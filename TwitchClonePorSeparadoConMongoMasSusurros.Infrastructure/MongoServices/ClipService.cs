using Microsoft.EntityFrameworkCore;
//using TwitchCloneMasMongo.Data;      // ← corregido
//using TwitchCloneMasMongo.Models;    // ← corregido

using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
//using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public class ClipService
    {
        private readonly AppDbContext _db;

        public ClipService(AppDbContext db)
        {
            _db = db;
        }

        public async Task<List<Clip>> GetClipsByChannelAsync(int channelId)
        {
            return await _db.Clips
                .Where(c => c.ChannelId == channelId)
                .Include(c => c.CreatedBy)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
        }

        public async Task<Clip> CreateClipAsync(
            int channelId, string userId, string title, string videoUrl, int durationSeconds)
        {
            var clip = new Clip
            {
                ChannelId = channelId,
                CreatedByUserId = userId,
                Title = title,
                VideoUrl = videoUrl,
                DurationSeconds = durationSeconds,
                Views = 0,
                CreatedAt = DateTime.Now
            };
            _db.Clips.Add(clip);
            await _db.SaveChangesAsync();
            return clip;
        }

        public async Task<Clip?> GetClipByIdAsync(int id)
        {
            return await _db.Clips
                .Include(c => c.Channel)
                .Include(c => c.CreatedBy)
                .FirstOrDefaultAsync(c => c.Id == id);
        }

        public async Task<bool> DeleteClipAsync(int id)
        {
            var clip = await _db.Clips.FindAsync(id);
            if (clip == null) return false;
            _db.Clips.Remove(clip);
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> IncrementViewsAsync(int id)
        {
            var clip = await _db.Clips.FindAsync(id);
            if (clip == null) return false;
            clip.Views++;
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<List<Clip>> GetPopularClipsAsync(int limit = 10)
        {
            return await _db.Clips
                .Include(c => c.Channel)
                .Include(c => c.CreatedBy)
                .OrderByDescending(c => c.Views)
                .Take(limit)
                .ToListAsync();
        }
    }
}