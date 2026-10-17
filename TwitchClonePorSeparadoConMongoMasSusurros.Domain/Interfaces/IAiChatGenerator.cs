namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    // Genera mensajes de chat "naturales" para los bots del simulador (Fase 3) con un LLM local.
    public interface IAiChatGenerator
    {
        bool IsAvailable { get; }

        // Devuelve una tanda de mensajes cortos acordes al stream y a lo que se está hablando.
        // Lista vacía si el modelo no respondió a tiempo.
        Task<IReadOnlyList<string>> GenerateAsync(string channelTitle, string category, IReadOnlyList<string> recentChat, int count, CancellationToken ct);
    }
}
