using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public class NotificationService : INotificationService
    {
        private readonly IMongoCollection<Notification> _notifications;

        public NotificationService(IMongoClient mongoClient, IOptions<MongoSettings> settings, ILogger<NotificationService> logger)
        {
            var database = mongoClient.GetDatabase(settings.Value.DatabaseName);
            _notifications = database.GetCollection<Notification>("Notifications");

            // TTL index: elimina notificaciones después de 30 días.
            // Se crea en segundo plano para no bloquear el arranque/DI si Mongo
            // no está disponible en este momento (ej. servicio detenido).
            var indexKey = Builders<Notification>.IndexKeys.Ascending(n => n.CreatedAt);
            var indexOptions = new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(30) };

            _ = Task.Run(async () =>
            {
                try
                {
                    await _notifications.Indexes.CreateOneAsync(new CreateIndexModel<Notification>(indexKey, indexOptions));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudo crear el índice TTL de Notifications. ¿Está MongoDB corriendo en el connection string configurado?");
                }
            });
        }

        public async Task CreateNotificationAsync(string userId, string message, string type, string? channelName, string? channelId)
        {
            var notification = new Notification
            {
                UserId = userId,
                Type = type,
                Message = message,
                ChannelName = channelName,
                ChannelId = channelId,
                IsRead = false,
                CreatedAt = DateTime.Now
            };
            await _notifications.InsertOneAsync(notification);
        }

        public async Task NotifyFollowersAsync(List<string> followerIds, string channelId, string channelName)
        {
            var notifications = followerIds.Select(userId => new Notification
            {
                UserId = userId,
                Type = "StreamLive",
                Message = $"{channelName} está en vivo ahora",
                ChannelId = channelId,
                ChannelName = channelName,
                CreatedAt = DateTime.Now
            }).ToList();

            if (notifications.Any())
                await _notifications.InsertManyAsync(notifications);
        }

        public async Task<List<Notification>> GetUserNotificationsAsync(string userId)
        {
            return await _notifications
                .Find(n => n.UserId == userId)
                .SortByDescending(n => n.CreatedAt)
                .Limit(50)
                .ToListAsync();
        }

        public async Task<List<Notification>> GetUnreadAsync(string userId)
        {
            return await _notifications
                .Find(n => n.UserId == userId && !n.IsRead)
                .SortByDescending(n => n.CreatedAt)
                .ToListAsync();
        }

        public async Task MarkAsReadAsync(string notificationId)
        {
            var filter = Builders<Notification>.Filter.Eq(n => n.Id, notificationId);
            var update = Builders<Notification>.Update.Set(n => n.IsRead, true);
            await _notifications.UpdateOneAsync(filter, update);
        }

        public async Task MarkAllAsReadAsync(string userId)
        {
            var filter = Builders<Notification>.Filter.Eq(n => n.UserId, userId);
            var update = Builders<Notification>.Update.Set(n => n.IsRead, true);
            await _notifications.UpdateManyAsync(filter, update);
        }

        public async Task<long> GetUnreadCountAsync(string userId)
        {
            return await _notifications.CountDocumentsAsync(n => n.UserId == userId && !n.IsRead);
        }

        public async Task DeleteNotificationAsync(string notificationId)
        {
            var filter = Builders<Notification>.Filter.Eq(n => n.Id, notificationId);
            await _notifications.DeleteOneAsync(filter);
        }
    }
}

