using Microsoft.EntityFrameworkCore;
//using TwitchCloneMasMongo.Data;
//using TwitchCloneMasMongo.Models;
//using TwitchCloneMasMongo.Services;

using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
//using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public class StreamService
    {
        private readonly AppDbContext _db;
        private readonly LiveMetricService _liveMetric;

        public StreamService(AppDbContext db, LiveMetricService liveMetric)
        {
            _db = db;
            _liveMetric = liveMetric;
        }

        // ─── Obtener canal de un usuario (1:1) ───────────────
        public async Task<Channel?> GetUserChannelAsync(string userId)
        {
            return await _db.Channels
                .Include(c => c.Category)
                .Include(c => c.Emotes)
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.UserId == userId);
        }

        // ─── Obtener todos los canales en vivo ───────────────
        public async Task<List<Channel>> GetLiveChannelsAsync()
        {
            var channels = await _db.Channels
                .Where(c => c.IsLive)
                .Include(c => c.User)
                .Include(c => c.Category)
                .ToListAsync();

            var metrics = await _liveMetric.GetLiveChannelsAsync();
            var metricsDict = metrics.ToDictionary(m => m.ChannelId);

            return channels
                .OrderByDescending(c => metricsDict.TryGetValue(c.Id.ToString(), out var m)
                    ? m.ViewerCount : 0)
                .ToList();
        }

        // ─── Obtener canales por categoría ──────────────────
        public async Task<List<Channel>> GetChannelsByCategoryAsync(int categoryId)
        {
            var channels = await _db.Channels
                .Where(c => c.CategoryId == categoryId)
                .Include(c => c.User)
                .Include(c => c.Category)
                .ToListAsync();

            var metrics = await _liveMetric.GetLiveChannelsAsync();
            var metricsDict = metrics.ToDictionary(m => m.ChannelId);

            return channels
                .OrderByDescending(c => metricsDict.TryGetValue(c.Id.ToString(), out var m)
                    ? m.ViewerCount : 0)
                .ToList();
        }

        // ─── Obtener canal por ID ──────────────────────────
        public async Task<Channel?> GetChannelByIdAsync(int channelId)
        {
            return await _db.Channels
                .Include(c => c.User)
                .Include(c => c.Category)
                .Include(c => c.Emotes)
                .FirstOrDefaultAsync(c => c.Id == channelId);
        }

        // ─── Actualizar estado en vivo ─────────────────────
        public async Task<bool> SetLiveStatusAsync(int channelId, bool isLive)
        {
            var channel = await _db.Channels
                .Include(c => c.User)
                .Include(c => c.Category)
                .FirstOrDefaultAsync(c => c.Id == channelId);

            if (channel == null) return false;

            channel.IsLive = isLive;
            await _db.SaveChangesAsync();

            if (isLive)
                await _liveMetric.StartStreamAsync(channelId.ToString());
            else
                await _liveMetric.StopStreamAsync(channelId.ToString());

            return true;
        }

        // ─── ViewerCount desde MongoDB ─────────────────────
        public async Task<int> GetViewerCountAsync(int channelId)
        {
            var metric = await _liveMetric.GetMetricAsync(channelId.ToString());
            return metric?.ViewerCount ?? 0;
        }

        // ─── Obtener canales recomendados ──────────────────
        public async Task<List<Channel>> GetRecommendedChannelsAsync(int limit = 10)
        {
            var channels = await _db.Channels
                .Include(c => c.User)
                .Include(c => c.Category)
                .ToListAsync();

            var metrics = await _liveMetric.GetLiveChannelsAsync();
            var metricsDict = metrics.ToDictionary(m => m.ChannelId);

            return channels
                .OrderByDescending(c => metricsDict.TryGetValue(c.Id.ToString(), out var m)
                    ? m.ViewerCount : 0)
                .Take(limit)
                .ToList();
        }

        // ─── Obtener clips de un canal ────────────────────
        public async Task<List<Clip>> GetChannelClipsAsync(int channelId)
        {
            return await _db.Clips
                .Where(c => c.ChannelId == channelId)
                .Include(c => c.CreatedBy)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
        }
    }
}