using Microsoft.Extensions.Options;
using MongoDB.Driver;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

//using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public class StreamSessionService : IStreamSessionService
    {
        private readonly IMongoCollection<StreamSession> _sessions;

        public StreamSessionService(IMongoClient mongoClient, IOptions<MongoSettings> settings)
        {
            var database = mongoClient.GetDatabase(settings.Value.DatabaseName);
            _sessions = database.GetCollection<StreamSession>("StreamSessions");
        }

        public async Task<StreamSession> StartSessionAsync(string channelId, string channelName, string title, string categoryName)
        {
            var session = new StreamSession
            {
                ChannelId = channelId,
                ChannelName = channelName,
                Title = title,
                CategoryName = categoryName,
                StartedAt = DateTime.UtcNow
            };

            await _sessions.InsertOneAsync(session);
            return session;
        }

        public async Task EndSessionAsync(string channelId, int peakViewers, int avgViewers)
        {
            var session = await _sessions
                .Find(s => s.ChannelId == channelId && s.EndedAt == null)
                .SortByDescending(s => s.StartedAt)
                .FirstOrDefaultAsync();

            if (session == null) return;

            var ended = DateTime.UtcNow;
            var duration = (long)(ended - session.StartedAt).TotalSeconds;

            var update = Builders<StreamSession>.Update
                .Set(s => s.EndedAt, ended)
                .Set(s => s.PeakViewers, peakViewers)
                .Set(s => s.AvgViewers, avgViewers)
                .Set(s => s.DurationSeconds, duration);

            await _sessions.UpdateOneAsync(s => s.Id == session.Id, update);
        }

        public async Task<List<StreamSession>> GetChannelHistoryAsync(string channelId, int limit = 20)
        {
            return await _sessions
                .Find(s => s.ChannelId == channelId && s.EndedAt != null)
                .SortByDescending(s => s.StartedAt)
                .Limit(limit)
                .ToListAsync();
        }

        public async Task<StreamSession?> GetActiveSessionAsync(string channelId)
        {
            return await _sessions
                .Find(s => s.ChannelId == channelId && s.EndedAt == null)
                .FirstOrDefaultAsync();
        }
    }
}

