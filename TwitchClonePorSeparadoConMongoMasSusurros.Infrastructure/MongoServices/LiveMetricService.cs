using Microsoft.Extensions.Options;
using MongoDB.Driver;


using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;



using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public class LiveMetricService : ILiveMetricService
    {
        private readonly IMongoCollection<LiveMetric> _metrics;

        public LiveMetricService(IMongoClient mongoClient, IOptions<MongoSettings> settings)
        {
            var database = mongoClient.GetDatabase(settings.Value.DatabaseName);
            _metrics = database.GetCollection<LiveMetric>("LiveMetrics");
        }

        public async Task StartStreamAsync(string channelId)
        {
            var metric = new LiveMetric
            {
                ChannelId = channelId,
                ViewerCount = 0,
                PeakViewers = 0,
                IsLive = true,
                UpdatedAt = DateTime.UtcNow
            };

            await _metrics.ReplaceOneAsync(
                m => m.ChannelId == channelId,
                metric,
                new ReplaceOptions { IsUpsert = true });
        }

        public async Task StopStreamAsync(string channelId)
        {
            var update = Builders<LiveMetric>.Update
                .Set(m => m.IsLive, false)
                .Set(m => m.ViewerCount, 0)
                .Set(m => m.UpdatedAt, DateTime.UtcNow);

            await _metrics.UpdateOneAsync(m => m.ChannelId == channelId, update);
        }

        public async Task IncrementViewersAsync(string channelId)
        {
            var update = Builders<LiveMetric>.Update
                .Inc(m => m.ViewerCount, 1)
                .Set(m => m.UpdatedAt, DateTime.UtcNow);

            var metric = await _metrics.FindOneAndUpdateAsync(
                m => m.ChannelId == channelId,
                update,
                new FindOneAndUpdateOptions<LiveMetric> { ReturnDocument = ReturnDocument.After });

            if (metric != null && metric.ViewerCount > metric.PeakViewers)
            {
                var peakUpdate = Builders<LiveMetric>.Update
                    .Set(m => m.PeakViewers, metric.ViewerCount);
                await _metrics.UpdateOneAsync(m => m.ChannelId == channelId, peakUpdate);
            }
        }

        public async Task DecrementViewersAsync(string channelId)
        {
            var update = Builders<LiveMetric>.Update
                .Inc(m => m.ViewerCount, -1)
                .Set(m => m.UpdatedAt, DateTime.UtcNow);

            await _metrics.UpdateOneAsync(m => m.ChannelId == channelId, update);
        }

        // Lo llama el PresenceBroadcaster cada ~15 s con el conteo real de viewers.
        // $max mantiene el pico sin tener que leer el documento antes.
        public async Task SetViewerCountAsync(string channelId, int viewers)
        {
            var update = Builders<LiveMetric>.Update
                .Set(m => m.ViewerCount, viewers)
                .Max(m => m.PeakViewers, viewers)
                .Set(m => m.UpdatedAt, DateTime.UtcNow);

            await _metrics.UpdateOneAsync(m => m.ChannelId == channelId && m.IsLive, update);
        }

        public async Task<LiveMetric?> GetMetricAsync(string channelId)
        {
            return await _metrics
                .Find(m => m.ChannelId == channelId)
                .FirstOrDefaultAsync();
        }

        public async Task<List<LiveMetric>> GetLiveChannelsAsync()
        {
            return await _metrics
                .Find(m => m.IsLive)
                .SortByDescending(m => m.ViewerCount)
                .ToListAsync();
        }
    }
}
