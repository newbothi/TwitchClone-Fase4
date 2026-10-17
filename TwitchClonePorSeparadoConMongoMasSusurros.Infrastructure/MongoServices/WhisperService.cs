using Microsoft.Extensions.Options;
using MongoDB.Driver;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public class WhisperService : IWhisperService
    {
        private readonly IMongoCollection<WhisperMessage> _whispers;

        public WhisperService(IMongoClient mongoClient, IOptions<MongoSettings> settings)
        {
            var database = mongoClient.GetDatabase(settings.Value.DatabaseName);
            _whispers = database.GetCollection<WhisperMessage>("WhisperMessages");

            // Índice por ConversationId: cada apertura de conversación filtra por él.
            var indexKey = Builders<WhisperMessage>.IndexKeys.Ascending(w => w.ConversationId).Descending(w => w.SentAt);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _whispers.Indexes.CreateOneAsync(new CreateIndexModel<WhisperMessage>(indexKey));
                }
                catch
                {
                    // Mismo criterio que NotificationService/AlertEventService: si Mongo no
                    // está disponible al arrancar, no tumbar el registro del servicio por esto.
                }
            });
        }

        public async Task<WhisperMessage> SendAsync(string senderId, string senderUserName, string recipientId, string recipientUserName, string content)
        {
            var whisper = new WhisperMessage
            {
                ConversationId = WhisperMessage.BuildConversationId(senderId, recipientId),
                SenderId = senderId,
                SenderUserName = senderUserName,
                RecipientId = recipientId,
                RecipientUserName = recipientUserName,
                Content = content,
                SentAt = DateTime.UtcNow
            };

            await _whispers.InsertOneAsync(whisper);
            return whisper;
        }

        public async Task<List<WhisperMessage>> GetConversationAsync(string conversationId, int limit = 100)
        {
            var messages = await _whispers
                .Find(w => w.ConversationId == conversationId && !w.IsDeleted)
                .SortByDescending(w => w.SentAt)
                .Limit(limit)
                .ToListAsync();

            messages.Reverse(); // orden cronológico ascendente para pintar el hilo de arriba hacia abajo
            return messages;
        }

        public async Task<List<WhisperConversationSummary>> GetConversationsForUserAsync(string userId)
        {
            // Trae el último mensaje de cada conversación en la que el usuario participa.
            // No hay demasiado volumen esperado por usuario, así que traemos todo lo
            // reciente y agrupamos en memoria en vez de armar un pipeline de agregación.
            var relevant = await _whispers
                .Find(w => (w.SenderId == userId || w.RecipientId == userId) && !w.IsDeleted)
                .SortByDescending(w => w.SentAt)
                .Limit(500)
                .ToListAsync();

            var summaries = new List<WhisperConversationSummary>();

            foreach (var group in relevant.GroupBy(w => w.ConversationId))
            {
                var last = group.First(); // ya viene ordenado desc por SentAt
                var isRecipient = last.RecipientId == userId;
                var otherUserId = last.SenderId == userId ? last.RecipientId : last.SenderId;
                var otherUserName = last.SenderId == userId ? last.RecipientUserName : last.SenderUserName;
                var unread = group.Count(w => w.RecipientId == userId && !w.IsRead);

                summaries.Add(new WhisperConversationSummary
                {
                    ConversationId = group.Key,
                    OtherUserId = otherUserId,
                    OtherUserName = otherUserName,
                    LastMessage = last.Content,
                    LastMessageAt = last.SentAt,
                    UnreadCount = unread
                });
            }

            return summaries.OrderByDescending(s => s.LastMessageAt).ToList();
        }

        public async Task MarkConversationAsReadAsync(string conversationId, string readerId)
        {
            var update = Builders<WhisperMessage>.Update.Set(w => w.IsRead, true);
            await _whispers.UpdateManyAsync(
                w => w.ConversationId == conversationId && w.RecipientId == readerId && !w.IsRead,
                update);
        }

        public async Task<int> GetUnreadCountAsync(string userId)
        {
            return (int)await _whispers.CountDocumentsAsync(
                w => w.RecipientId == userId && !w.IsRead && !w.IsDeleted);
        }
    }
}
