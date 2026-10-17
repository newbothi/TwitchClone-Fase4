using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    // Crea los índices de MongoDB al arrancar (es idempotente: si ya existen, no hace nada).
    // Sin el índice { ChannelId, SentAt }, cargar el historial de un canal recorre
    // la colección entera a medida que crece.
    public class MongoIndexInitializer : IHostedService
    {
        private readonly IMongoClient _client;
        private readonly MongoSettings _settings;
        private readonly ILogger<MongoIndexInitializer> _logger;

        public MongoIndexInitializer(IMongoClient client, IOptions<MongoSettings> settings, ILogger<MongoIndexInitializer> logger)
        {
            _client = client;
            _settings = settings.Value;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // En segundo plano: si Mongo está apagado, la app arranca igual.
            _ = Task.Run(async () =>
            {
                try
                {
                    var db = _client.GetDatabase(_settings.DatabaseName);
                    var chat = db.GetCollection<ChatMessage>(ChatMessageService.CollectionName);

                    await chat.Indexes.CreateOneAsync(new CreateIndexModel<ChatMessage>(
                        Builders<ChatMessage>.IndexKeys.Ascending(m => m.ChannelId).Descending(m => m.SentAt),
                        new CreateIndexOptions { Name = "ChannelId_SentAt" }));

                    _logger.LogInformation("Índices de MongoDB verificados.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudieron crear los índices de MongoDB. ¿Está MongoDB corriendo?");
                }
            }, CancellationToken.None);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
