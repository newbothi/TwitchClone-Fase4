using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes
{
    // Reemplaza a ChatMessageService (colección ChatMessages de MongoDB).
    public sealed class InMemoryChatMessageService : IChatMessageService
    {
        private readonly InMemoryPlatform _db;

        public InMemoryChatMessageService(InMemoryPlatform db) => _db = db;

        public Task SaveMessageAsync(ChatMessage message)
        {
            if (_db.FailChatSaves) throw new InvalidOperationException("MongoDB no disponible (simulado).");
            lock (_db.Messages) _db.Messages.Add(message);
            return Task.CompletedTask;
        }

        // La versión real escribe en lote cada ~200 ms; aquí todo se guarda al instante.
        public Task FlushAsync() => Task.CompletedTask;

        public Task<List<ChatMessage>> GetRecentMessagesAsync(string channelId, int limit = 50)
        {
            lock (_db.Messages)
            {
                var recent = _db.Messages
                    .Where(m => m.ChannelId == channelId && !m.IsDeleted)
                    .OrderBy(m => m.SentAt)
                    .ToList();
                return Task.FromResult(recent.Skip(Math.Max(0, recent.Count - limit)).ToList());
            }
        }

        public Task<ChatMessage?> GetByIdAsync(string messageId)
        {
            lock (_db.Messages) return Task.FromResult(_db.Messages.FirstOrDefault(m => m.Id == messageId));
        }

        public Task DeleteMessageAsync(string messageId) =>
            MarkDeleted(m => m.Id == messageId);

        public Task DeleteUserMessagesAsync(string channelId, string userId) =>
            MarkDeleted(m => m.ChannelId == channelId && m.UserId == userId);

        public Task ClearChannelAsync(string channelId) =>
            MarkDeleted(m => m.ChannelId == channelId);

        private Task MarkDeleted(Func<ChatMessage, bool> predicate)
        {
            lock (_db.Messages)
            {
                foreach (var message in _db.Messages.Where(predicate)) message.IsDeleted = true;
            }
            return Task.CompletedTask;
        }
    }
}
