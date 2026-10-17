using System.Text.Json.Serialization;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs
{
    // Lo que viaja por SignalR (y lo que se usa para pintar el historial).
    // SignalR lo serializa en camelCase: id, channelId, userId, displayName, ...
    public class ChatMessageDto
    {
        public string Id { get; set; } = string.Empty;
        public int ChannelId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Color { get; set; } = "#a970ff";
        public List<string> Badges { get; set; } = new();
        public List<ChatFragment> Fragments { get; set; } = new();
        public DateTime SentAt { get; set; }

        // Fase 2
        public bool IsAction { get; set; }
        public bool IsAnnouncement { get; set; }
        public ChatReplyInfo? ReplyTo { get; set; }

        // Texto plano (para respuestas, mensajes fijados y el simulador).
        public string PlainText() => string.Concat(Fragments.Select(f => f.Text));
    }

    // Referencia al mensaje al que se responde (se guarda copiada: si el original
    // se borra, la respuesta sigue mostrando a quién respondía).
    public class ChatReplyInfo
    {
        public string MessageId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    // Resultado de intentar enviar un mensaje (o un comando).
    // Effects: lo que el hub tiene que difundir (mensaje nuevo, ban, encuesta, raid...).
    public class ChatSendResult
    {
        public bool Ok { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
        public int? RetryAfterSeconds { get; init; }
        public ChatMessageDto? Message { get; init; }

        // Respuesta privada para quien escribió (resultado de un comando, "en revisión"...).
        public string? Notice { get; init; }

        // true si AutoMod retuvo el mensaje para revisión.
        public bool HeldForReview { get; init; }

        // No viaja al navegador: lo consume el hub.
        [JsonIgnore]
        public List<ChatEffect> Effects { get; init; } = new();

        public static ChatSendResult Success(ChatMessageDto message) =>
            new() { Ok = true, Message = message, Effects = { new BroadcastMessageEffect(message) } };

        public static ChatSendResult FromAction(ChatActionResult action) => action.Ok
            ? new() { Ok = true, Notice = action.Notice, Effects = action.Effects }
            : Fail("command_error", action.Error ?? "No se pudo ejecutar el comando.");

        public static ChatSendResult Fail(string code, string message, int? retryAfterSeconds = null) =>
            new() { Ok = false, ErrorCode = code, ErrorMessage = message, RetryAfterSeconds = retryAfterSeconds };
    }

    // Resultado de una acción de moderación o de un comando.
    public class ChatActionResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public string? Notice { get; init; }
        public string? TargetDisplayName { get; init; }

        [JsonIgnore]
        public List<ChatEffect> Effects { get; init; } = new();

        public static ChatActionResult Success(string? notice = null, params ChatEffect[] effects) =>
            new() { Ok = true, Notice = notice, Effects = effects.ToList() };

        public static ChatActionResult Fail(string error) => new() { Ok = false, Error = error };
    }

    // Modos del chat visibles para todos (las palabras bloqueadas no se exponen).
    public class ChatSettingsDto
    {
        public int SlowModeSeconds { get; set; }
        public bool FollowersOnly { get; set; }
        public bool EmoteOnly { get; set; }
        public string Rules { get; set; } = string.Empty;
    }

    // Configuración completa, solo para el dueño del canal (Dashboard).
    public class ChatModerationSettingsDto : ChatSettingsDto
    {
        public List<string> BannedWords { get; set; } = new();
        public bool BlockLinks { get; set; } = true;
        public bool AutoModEnabled { get; set; }

        // Fase 4
        public bool BlockExcessiveCaps { get; set; }
        public bool BlockRepeatedChars { get; set; }
    }

    // Identidad de quien escribe: SIEMPRE sale de la cookie/claims del servidor,
    // nunca de un parámetro que mande el navegador.
    public record ChatSender(string UserId, bool IsAdmin, bool IsSimulated = false);

    // Perfil cacheado de un usuario para el chat.
    public record ChatUserProfile(string UserId, string DisplayName, bool IsSuspended, DateTime CreatedAt);

    // Mensaje retenido por AutoMod esperando revisión de un moderador.
    public class AutoModHeldDto
    {
        public string Id { get; set; } = string.Empty;
        public int ChannelId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public DateTime HeldAt { get; set; }
    }

    // Tarjeta que aparece al hacer clic en un nombre del chat.
    public class UserCardDto
    {
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Color { get; set; } = "#a970ff";
        public List<string> Badges { get; set; } = new();
        public string Role { get; set; } = "viewer"; // broadcaster | moderator | vip | viewer
        public DateTime AccountCreatedAt { get; set; }
        public DateTime? FollowingSince { get; set; }

        // Solo se llenan si quien mira la tarjeta puede moderar.
        public bool IsBanned { get; set; }
        public DateTime? TimedOutUntil { get; set; }

        // Qué botones mostrar.
        public bool CanTimeout { get; set; }
        public bool CanBan { get; set; }
        public bool CanManageRoles { get; set; }
    }

    public class PollDto
    {
        public string Id { get; set; } = string.Empty;
        public int ChannelId { get; set; }
        public string Question { get; set; } = string.Empty;
        public List<PollOptionDto> Options { get; set; } = new();
        public int TotalVotes { get; set; }
        public DateTime EndsAt { get; set; }
        public bool Ended { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
    }

    public class PollOptionDto
    {
        public string Text { get; set; } = string.Empty;
        public int Votes { get; set; }
    }

    // Mod/VIP de un canal (Dashboard y /mods, /vips).
    public class ChannelRoleEntry
    {
        public int ChannelId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public ChannelRoleType Role { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    // Usuario encontrado por su nombre público (para comandos como /ban NombreDeUsuario).
    public class ChatUserLookup
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayUsername { get; set; } = string.Empty;
    }
}
