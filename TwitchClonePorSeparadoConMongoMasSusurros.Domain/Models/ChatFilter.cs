using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    // Configuración del chat de un canal (colección "ChatFilters" en MongoDB).
    // Los campos de la Fase 2 no existen en documentos antiguos: al leerlos quedan
    // con el valor por defecto de abajo (por ejemplo, BlockLinks = true).
    [BsonIgnoreExtraElements]
    public class ChatFilter
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;
        public string ChannelId { get; set; } = null!;
        public List<string> BannedWords { get; set; } = new();
        public bool SlowModeEnabled { get; set; } = false;
        public int SlowModeSeconds { get; set; } = 0;
        public bool FollowersOnlyMode { get; set; } = false;
        public bool SubsOnlyMode { get; set; } = false;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // ── Fase 2 ─────────────────────────────────────────────
        // Solo emotes: los viewers solo pueden mandar mensajes hechos de emotes.
        public bool EmoteOnlyMode { get; set; } = false;

        // Bloquear enlaces (los VIP, mods y el dueño sí pueden enviarlos). Como en Twitch, activo por defecto.
        public bool BlockLinks { get; set; } = true;

        // AutoMod con IA: los mensajes dudosos quedan retenidos hasta que un mod los apruebe.
        public bool AutoModEnabled { get; set; } = false;

        // Reglas del chat que se muestran la primera vez que alguien va a escribir.
        public string Rules { get; set; } = string.Empty;

        // Mensaje fijado arriba del chat.
        public ChatMessageDto? PinnedMessage { get; set; }

        // ── Fase 4 ─────────────────────────────────────────────
        // Las dos reglas del score_heuristic del microservicio Python original, ahora en C#.
        // Desactivadas por defecto: cada canal decide en el Dashboard.
        // Exceso de mayúsculas: más de 10 letras y más del 80 % en mayúsculas.
        public bool BlockExcessiveCaps { get; set; } = false;

        // Caracteres repetidos: 7 o más iguales seguidos ("holaaaaaaa", "!!!!!!!").
        public bool BlockRepeatedChars { get; set; } = false;
    }
}
