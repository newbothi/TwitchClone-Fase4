using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    // Susurro / mensaje privado 1:1 entre dos usuarios (equivalente a los
    // "whispers" de Twitch). Vive en MongoDB, igual que ChatMessage, porque
    // es texto de alto volumen sin necesidad de joins relacionales.
    public class WhisperMessage
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;

        // Id de conversación estable entre dos usuarios: los dos UserId
        // ordenados alfabéticamente y unidos con "_", para que sin importar
        // quién le escribe a quién, ambos caigan en la misma conversación.
        // Ej: "abc123_xyz789". Se calcula con WhisperMessage.BuildConversationId.
        public string ConversationId { get; set; } = null!;

        public string SenderId { get; set; } = null!;
        public string SenderUserName { get; set; } = null!;
        public string RecipientId { get; set; } = null!;
        public string RecipientUserName { get; set; } = null!;

        public string Content { get; set; } = null!;
        public DateTime SentAt { get; set; } = DateTime.UtcNow;
        public bool IsRead { get; set; } = false;
        public bool IsDeleted { get; set; } = false;

        public static string BuildConversationId(string userIdA, string userIdB)
        {
            var ids = new[] { userIdA, userIdB };
            Array.Sort(ids, StringComparer.Ordinal);
            return $"{ids[0]}_{ids[1]}";
        }
    }
}
