using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    // Mensajes del chat en MongoDB, con escritura por lotes.
    //
    // SaveMessageAsync solo encola; ChatMessageWriterService vacía la cola cada 250 ms
    // con un InsertMany. Con 300 bots escribiendo (Fase 3) son ~4 escrituras por
    // segundo en vez de cientos. Antes de borrar o consultar se llama FlushAsync para
    // que nada quede "en el aire".
    public class ChatMessageService : IChatMessageService
    {
        public const string CollectionName = "ChatMessages";
        private const int MaxBatch = 500;

        private readonly IMongoCollection<ChatMessage> _messages;
        private readonly ILogger<ChatMessageService> _logger;
        private readonly System.Threading.Channels.Channel<ChatMessage> _queue = System.Threading.Channels.Channel.CreateBounded<ChatMessage>(
            new System.Threading.Channels.BoundedChannelOptions(50_000) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite, SingleReader = false });
        private readonly SemaphoreSlim _flushLock = new(1, 1);

        public ChatMessageService(IMongoClient mongoClient, IOptions<MongoSettings> settings, ILogger<ChatMessageService> logger)
        {
            var database = mongoClient.GetDatabase(settings.Value.DatabaseName);
            _messages = database.GetCollection<ChatMessage>(CollectionName);
            _logger = logger;
        }

        public Task SaveMessageAsync(ChatMessage message)
        {
            if (!_queue.Writer.TryWrite(message))
                _logger.LogWarning("Cola de mensajes de chat llena: MongoDB no está aceptando escrituras. Se descarta un mensaje.");
            return Task.CompletedTask;
        }

        // Nunca lanza excepción: si Mongo falla, se registra y el chat sigue funcionando.
        public async Task FlushAsync()
        {
            await _flushLock.WaitAsync();
            try
            {
                var batch = new List<ChatMessage>(MaxBatch);
                while (_queue.Reader.TryRead(out var message))
                {
                    batch.Add(message);
                    if (batch.Count >= MaxBatch)
                    {
                        await InsertBatchAsync(batch);
                        batch.Clear();
                    }
                }
                if (batch.Count > 0) await InsertBatchAsync(batch);
            }
            finally
            {
                _flushLock.Release();
            }
        }

        private async Task InsertBatchAsync(List<ChatMessage> batch)
        {
            try
            {
                await _messages.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudieron guardar {Count} mensajes de chat en MongoDB. ¿Está MongoDB corriendo?", batch.Count);
            }
        }

        public async Task<List<ChatMessage>> GetRecentMessagesAsync(string channelId, int limit = 50)
        {
            // Se piden los N más recientes (índice ChannelId + SentAt desc) y se invierten
            // para devolverlos en orden cronológico.
            var latest = await _messages
                .Find(m => m.ChannelId == channelId && !m.IsDeleted)
                .SortByDescending(m => m.SentAt)
                .Limit(limit)
                .ToListAsync();

            latest.Reverse();
            return latest;
        }

        public async Task<ChatMessage?> GetByIdAsync(string messageId)
        {
            return await _messages
                .Find(m => m.Id == messageId)
                .FirstOrDefaultAsync();
        }

        public async Task DeleteMessageAsync(string messageId)
        {
            var update = Builders<ChatMessage>.Update.Set(m => m.IsDeleted, true);
            await _messages.UpdateOneAsync(m => m.Id == messageId, update);
        }

        public async Task DeleteUserMessagesAsync(string channelId, string userId)
        {
            var update = Builders<ChatMessage>.Update.Set(m => m.IsDeleted, true);
            await _messages.UpdateManyAsync(
                m => m.ChannelId == channelId && m.UserId == userId && !m.IsDeleted, update);
        }

        public async Task ClearChannelAsync(string channelId)
        {
            var update = Builders<ChatMessage>.Update.Set(m => m.IsDeleted, true);
            await _messages.UpdateManyAsync(
                m => m.ChannelId == channelId && !m.IsDeleted, update);
        }
    }

    // Vacía la cola de ChatMessageService cada 250 ms y una última vez al apagar la app.
    public class ChatMessageWriterService : BackgroundService
    {
        private readonly IChatMessageService _messages;

        public ChatMessageWriterService(IChatMessageService messages) => _messages = messages;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                    await _messages.FlushAsync();
            }
            catch (OperationCanceledException) { }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            await _messages.FlushAsync();
        }
    }
}
