using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    // Documento de la colección "ChatMessages" en MongoDB.
    // Los campos nuevos (Fase 1 y 2) no existen en mensajes antiguos: se leen igual,
    // con sus valores por defecto, y el servidor los completa al armar el historial.
    [BsonIgnoreExtraElements]
    public class ChatMessage
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;
        public string ChannelId { get; set; } = null!;
        public string UserId { get; set; } = null!;
        public string UserName { get; set; } = null!;
        public string Content { get; set; } = null!;
        public DateTime SentAt { get; set; } = DateTime.UtcNow;
        public bool IsDeleted { get; set; } = false;
        public int Bits { get; set; } = 0;

        // Color del nombre en el chat (hex), calculado al enviar.
        public string? Color { get; set; }

        // Insignias al momento de enviar: "broadcaster", "moderator", "vip", "admin".
        public List<string> Badges { get; set; } = new();

        // Mensaje ya separado en texto y emotes, tal como se mostró en vivo.
        public List<ChatFragment> Fragments { get; set; } = new();

        // ── Fase 2 ─────────────────────────────────────────────
        public bool IsAction { get; set; }          // /me
        public bool IsAnnouncement { get; set; }    // /announce
        public ChatReplyInfo? ReplyTo { get; set; } // respuesta a otro mensaje
        public bool IsSimulated { get; set; }       // enviado por un bot del simulador (Fase 3)
    }
}
