using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    // Preferencias de chat por usuario (por ahora, el color elegido con /color).
    public class ChatUserSettingsService : IChatUserSettingsService
    {
        private readonly IMongoCollection<ChatUserSettings> _settings;

        public ChatUserSettingsService(IMongoClient mongoClient, IOptions<MongoSettings> settings)
        {
            var database = mongoClient.GetDatabase(settings.Value.DatabaseName);
            _settings = database.GetCollection<ChatUserSettings>("ChatUserSettings");
        }

        public async Task<string?> GetColorAsync(string userId)
        {
            var doc = await _settings.Find(s => s.UserId == userId).FirstOrDefaultAsync();
            return doc?.Color;
        }

        public async Task SetColorAsync(string userId, string? color)
        {
            await _settings.UpdateOneAsync(
                s => s.UserId == userId,
                Builders<ChatUserSettings>.Update.Set(s => s.Color, color).Set(s => s.UpdatedAt, DateTime.UtcNow),
                new UpdateOptions { IsUpsert = true });
        }

        [BsonIgnoreExtraElements]
        public class ChatUserSettings
        {
            [BsonId]
            [BsonRepresentation(BsonType.ObjectId)]
            public string Id { get; set; } = null!;
            public string UserId { get; set; } = null!;
            public string? Color { get; set; }
            public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        }
    }
}
