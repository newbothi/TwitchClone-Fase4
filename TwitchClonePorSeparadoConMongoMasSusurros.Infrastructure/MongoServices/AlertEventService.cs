using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public class AlertEventService : IAlertEventService
    {
        private readonly IMongoCollection<AlertEvent> _alerts;

        public AlertEventService(IMongoClient mongoClient, IOptions<MongoSettings> settings, ILogger<AlertEventService> logger)
        {
            var database = mongoClient.GetDatabase(settings.Value.DatabaseName);
            _alerts = database.GetCollection<AlertEvent>("AlertEvents");

            // TTL index: elimina alertas después de 7 días.
            // Se crea en segundo plano para no bloquear el arranque/DI si Mongo
            // no está disponible en este momento (ej. servicio detenido).
            var indexKey = Builders<AlertEvent>.IndexKeys.Ascending(a => a.CreatedAt);
            var indexOptions = new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(7) };

            _ = Task.Run(async () =>
            {
                try
                {
                    await _alerts.Indexes.CreateOneAsync(new CreateIndexModel<AlertEvent>(indexKey, indexOptions));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudo crear el índice TTL de AlertEvents. ¿Está MongoDB corriendo en el connection string configurado?");
                }
            });
        }

        public async Task CreateAlertAsync(string channelId, string type,
            string fromUserName, string message, int amount = 0)
        {
            var alert = new AlertEvent
            {
                ChannelId = channelId,
                Type = type,
                FromUserName = fromUserName,
                Message = message,
                Amount = amount,
                CreatedAt = DateTime.UtcNow
            };

            await _alerts.InsertOneAsync(alert);
        }

        public async Task<List<AlertEvent>> GetPendingAlertsAsync(string channelId)
        {
            return await _alerts
                .Find(a => a.ChannelId == channelId && !a.IsShown)
                .SortBy(a => a.CreatedAt)
                .ToListAsync();
        }

        public async Task MarkAsShownAsync(string alertId)
        {
            var update = Builders<AlertEvent>.Update.Set(a => a.IsShown, true);
            await _alerts.UpdateOneAsync(a => a.Id == alertId, update);
        }

        public async Task<List<AlertEvent>> GetAlertHistoryAsync(string channelId, int limit = 50)
        {
            return await _alerts
                .Find(a => a.ChannelId == channelId)
                .SortByDescending(a => a.CreatedAt)
                .Limit(limit)
                .ToListAsync();
        }
    }
}

