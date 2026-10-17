using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Chat
{
    // "Efectos" que produce el chat. El servicio de chat (Application) decide QUÉ pasa;
    // el ChatEffectDispatcher (Web) decide CÓMO se difunde por SignalR.
    // Así el mismo efecto sirve para un comando (/ban), un botón de la UI, el Dashboard
    // o el simulador de bots, y se puede probar sin SignalR.
    public abstract record ChatEffect;

    // Mensaje nuevo para todo el canal.
    public sealed record BroadcastMessageEffect(ChatMessageDto Message) : ChatEffect;

    public sealed record MessageDeletedEffect(string MessageId) : ChatEffect;

    // Timeout (Seconds > 0) o ban (IsBan). Se borran sus mensajes en vivo.
    public sealed record UserSanctionedEffect(string UserId, string DisplayName, bool IsBan, int Seconds) : ChatEffect;

    public sealed record UserUnbannedEffect(string UserId, string DisplayName) : ChatEffect;

    public sealed record ChatClearedEffect : ChatEffect;

    public sealed record SettingsChangedEffect(ChatSettingsDto Settings) : ChatEffect;

    public sealed record RoleChangedEffect(string UserId, string DisplayName, ChannelRoleType Role) : ChatEffect;

    // Message = null → se quitó el mensaje fijado.
    public sealed record PinChangedEffect(ChatMessageDto? Message) : ChatEffect;

    // Solo para los moderadores del canal.
    public sealed record AutoModHeldEffect(AutoModHeldDto Held) : ChatEffect;

    public sealed record AutoModResolvedEffect(string HeldId, string UserId, bool Approved, string ModeratorName) : ChatEffect;

    // Encuesta creada, actualizada o terminada.
    public sealed record PollEffect(PollDto Poll) : ChatEffect;

    // El canal actual manda a sus viewers a otro canal.
    public sealed record RaidEffect(int SourceChannelId, string SourceName, int TargetChannelId, string TargetName, int Viewers) : ChatEffect;

    // Susurro enviado con /w.
    public sealed record WhisperEffect(string RecipientId, string ConversationId, string SenderName, string Content) : ChatEffect;

    // Aviso para un usuario puntual (por ejemplo, "tu mensaje fue rechazado por AutoMod").
    public sealed record UserNoticeEffect(string UserId, string Text) : ChatEffect;

    // Fase 4: una decisión de un moderador que sirve para entrenar a AutoMod.
    // No viaja al navegador: la consume AutoModFeedbackObserver y la manda al servicio Python.
    //   Source: "automod_aprobado" | "automod_rechazado" | "mensaje_borrado"
    public sealed record AutoModFeedbackEffect(string Text, bool IsToxic, string? Category, string Source) : ChatEffect
    {
        public const string Approved = "automod_aprobado";
        public const string Rejected = "automod_rechazado";
        public const string Deleted = "mensaje_borrado";
    }
}
