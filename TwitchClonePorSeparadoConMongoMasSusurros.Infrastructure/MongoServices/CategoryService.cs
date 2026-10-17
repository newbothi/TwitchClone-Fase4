using Microsoft.EntityFrameworkCore;

//using TwitchCloneMasMongo.Data;
//using TwitchCloneMasMongo.Models;
//using TwitchCloneMasMongo.Services;

using Microsoft.Extensions.Options;
using MongoDB.Driver;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;

using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
//using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public class CategoryService
    {
        private readonly AppDbContext _db;
        private readonly LiveMetricService _liveMetric;

        public CategoryService(AppDbContext db, LiveMetricService liveMetric)
        {
            _db = db;
            _liveMetric = liveMetric;
        }

        // Todas las categorías — sin cambios
        public async Task<List<Category>> GetAllCategoriesAsync()
        {
            return await _db.Categories
                .OrderBy(c => c.Name)
                .ToListAsync();
        }

        // Por ID — sin cambios
        public async Task<Category?> GetCategoryByIdAsync(int id)
        {
            return await _db.Categories
                .Include(c => c.Channels)
                .FirstOrDefaultAsync(c => c.Id == id);
        }

        // ✅ Ahora ordena por ViewerCount desde MongoDB
        public async Task<List<Channel>> GetLiveChannelsByCategoryAsync(int categoryId)
        {
            var channels = await _db.Channels
                .Where(c => c.CategoryId == categoryId && c.IsLive)
                .Include(c => c.User)
                .ToListAsync();

            var metrics = await _liveMetric.GetLiveChannelsAsync();
            var metricsDict = metrics.ToDictionary(m => m.ChannelId);

            return channels
                .OrderByDescending(c => metricsDict.TryGetValue(c.Id.ToString(), out var m)
                    ? m.ViewerCount : 0)
                .ToList();
        }
    }
}