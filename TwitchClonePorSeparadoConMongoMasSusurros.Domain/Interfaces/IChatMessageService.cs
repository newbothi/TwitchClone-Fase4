using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    public interface IChatMessageService
    {
        // Encola el mensaje: se escribe en MongoDB en lotes (InsertMany) cada ~250 ms.
        // Con cientos de mensajes por segundo (simulador, prueba de carga) esto evita
        // un viaje a la base por mensaje.
        Task SaveMessageAsync(ChatMessage message);

        // Fuerza la escritura de lo que esté en cola (antes de borrar mensajes, por ejemplo).
        Task FlushAsync();

        // Devuelve los últimos N mensajes en orden cronológico (el más antiguo primero),
        // listo para pintarse de arriba hacia abajo.
        Task<List<ChatMessage>> GetRecentMessagesAsync(string channelId, int limit = 50);

        Task<ChatMessage?> GetByIdAsync(string messageId);
        Task DeleteMessageAsync(string messageId);
        Task DeleteUserMessagesAsync(string channelId, string userId);

        // Marca como borrados todos los mensajes actuales del canal (/clear).
        Task ClearChannelAsync(string channelId);
    }
}
