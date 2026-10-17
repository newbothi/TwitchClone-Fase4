using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    // Toda la lógica del chat en vivo. El hub de SignalR solo traduce llamadas del
    // navegador a este servicio y difunde los "efectos" que devuelve (ChatEffect).
    // El simulador de bots (Fase 3) usa exactamente el mismo servicio.
    public interface IChatService
    {
        // Mensaje normal, /me o comando (/ban, /slow, /poll, /raid, /w, ...).
        // Pipeline: comando → suspensión → ban/timeout → solo seguidores → solo emotes →
        // enlaces → palabras bloqueadas → repetidos → modo lento → límite → AutoMod → guardar.
        Task<ChatSendResult> SendAsync(ChatSender sender, int channelId, string content, string? replyToId = null, CancellationToken ct = default);

        Task<List<ChatMessageDto>> GetHistoryAsync(int channelId, int limit = 50);

        // ── Configuración ──────────────────────────────────────────────────
        Task<ChatSettingsDto> GetSettingsAsync(int channelId);
        Task<ChatModerationSettingsDto> GetModerationSettingsAsync(int channelId);
        Task<ChatActionResult> UpdateSettingsAsync(int channelId, ChatModerationSettingsDto settings);

        // ── Usuarios y roles ───────────────────────────────────────────────
        Task<string?> GetChannelOwnerIdAsync(int channelId);
        Task<ChatUserProfile?> GetUserProfileAsync(string userId);
        Task<ChannelRoleType> GetChannelRoleAsync(int channelId, string userId);

        // Dueño del canal, moderadores del canal y Admin de la plataforma.
        Task<bool> CanModerateAsync(int channelId, ChatSender user);

        Task<UserCardDto?> GetUserCardAsync(int channelId, string targetUserId, ChatSender? viewer);

        // ── Moderación (validan permisos dentro del servicio) ─────────────
        Task<ChatActionResult> DeleteMessageAsync(int channelId, string messageId, ChatSender moderator);
        Task<ChatActionResult> TimeoutAsync(int channelId, string targetUserId, int seconds, string? reason, ChatSender moderator);
        Task<ChatActionResult> BanAsync(int channelId, string targetUserId, string? reason, ChatSender moderator);
        Task<ChatActionResult> UnbanAsync(int channelId, string targetUserId, ChatSender moderator);
        Task<ChatActionResult> ClearAsync(int channelId, ChatSender moderator);

        // Solo el dueño del canal o un Admin pueden nombrar/quitar mods y VIP.
        Task<ChatActionResult> SetRoleAsync(int channelId, string targetUserId, ChannelRoleType role, ChatSender actor);

        // ── Mensaje fijado ─────────────────────────────────────────────────
        Task<ChatMessageDto?> GetPinnedAsync(int channelId);
        Task<ChatActionResult> PinAsync(int channelId, string messageId, ChatSender moderator);
        Task<ChatActionResult> UnpinAsync(int channelId, ChatSender moderator);

        // ── AutoMod ────────────────────────────────────────────────────────
        IReadOnlyList<AutoModHeldDto> GetHeldMessages(int channelId);
        Task<ChatActionResult> ResolveAutoModAsync(int channelId, string heldId, bool approve, ChatSender moderator);

        // ── Encuestas ──────────────────────────────────────────────────────
        PollDto? GetActivePoll(int channelId);
        Task<ChatActionResult> VotePollAsync(int channelId, string pollId, int optionIndex, ChatSender voter);

        // Llamar cuando cambian emotes del canal, para no esperar a que venza la caché.
        void InvalidateChannel(int channelId);
    }
}
