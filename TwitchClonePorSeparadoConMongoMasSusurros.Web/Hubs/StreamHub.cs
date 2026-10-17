using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs
{
    // Hub único de la app: chat del canal, presencia de viewers y eventos en vivo.
    //
    // Reglas de seguridad:
    //  • El navegador solo manda datos (channelId, texto, ids). Quién es sale de la cookie
    //    (Context.UserIdentifier), nunca de un parámetro: no se puede suplantar a nadie.
    //  • Todo lo que se difunde es un DTO con fragmentos de texto/emote; el cliente lo pinta
    //    con textContent.
    //  • Los permisos (moderar, nombrar mods, raidear...) los valida IChatService.
    //
    // El hub no decide nada: llama a IChatService y entrega los "efectos" que devuelve al
    // ChatEffectDispatcher, que es quien los difunde (ver ChatEffectDispatcher.cs).
    public class StreamHub : Hub
    {
        private readonly IChatService _chat;
        private readonly IChatUserRepository _chatUsers;
        private readonly PresenceTracker _presence;
        private readonly ChatEffectDispatcher _dispatcher;
        private readonly ILogger<StreamHub> _logger;

        public StreamHub(IChatService chat, IChatUserRepository chatUsers, PresenceTracker presence,
            ChatEffectDispatcher dispatcher, ILogger<StreamHub> logger)
        {
            _chat = chat;
            _chatUsers = chatUsers;
            _presence = presence;
            _dispatcher = dispatcher;
            _logger = logger;
        }

        public static string ChannelGroup(int channelId) => $"channel_{channelId}";
        public static string ModsGroup(int channelId) => $"mods_{channelId}";

        // ── Presencia ──────────────────────────────────────────────────────

        public async Task<JoinChannelResult> JoinChannel(int channelId)
        {
            var owner = await _chat.GetChannelOwnerIdAsync(channelId);
            if (owner == null) throw new HubException("El canal no existe.");

            var sender = CurrentSender();
            string viewerKey;
            string? displayName = null;

            if (sender != null)
            {
                viewerKey = PresenceTracker.UserKey(sender.UserId);
                displayName = (await _chat.GetUserProfileAsync(sender.UserId))?.DisplayName;
            }
            else
            {
                viewerKey = PresenceTracker.AnonymousKey(Context.ConnectionId);
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, ChannelGroup(channelId));
            var previous = _presence.Join(channelId, Context.ConnectionId, viewerKey, displayName);
            if (previous is int old)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, ChannelGroup(old));
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, ModsGroup(old));
            }

            ChatSettingsDto settings;
            ChatMessageDto? pinned = null;
            try
            {
                settings = await _chat.GetSettingsAsync(channelId);
                pinned = await _chat.GetPinnedAsync(channelId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo leer la configuración del chat del canal {ChannelId}.", channelId);
                settings = new ChatSettingsDto();
            }

            var canModerate = sender != null && await _chat.CanModerateAsync(channelId, sender);
            var role = sender == null ? "viewer"
                : owner == sender.UserId ? "broadcaster"
                : ChatEffectDispatcher.RoleName(await _chat.GetChannelRoleAsync(channelId, sender.UserId)) switch { "none" => "viewer", var r => r };

            // Los moderadores reciben además los mensajes retenidos por AutoMod.
            if (canModerate) await Groups.AddToGroupAsync(Context.ConnectionId, ModsGroup(channelId));
            else await Groups.RemoveFromGroupAsync(Context.ConnectionId, ModsGroup(channelId));

            var points = 0;
            if (sender != null)
            {
                try { points = await _chatUsers.GetPointsAsync(sender.UserId); }
                catch (Exception ex) { _logger.LogDebug(ex, "No se pudieron leer los puntos."); }
            }

            return new JoinChannelResult(
                _presence.GetCount(channelId),
                canModerate,
                role,
                settings,
                pinned,
                _chat.GetActivePoll(channelId),
                canModerate ? _chat.GetHeldMessages(channelId) : Array.Empty<AutoModHeldDto>(),
                points);
        }

        public async Task LeaveChannel(int channelId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, ChannelGroup(channelId));
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, ModsGroup(channelId));
            _presence.Leave(Context.ConnectionId);
        }

        public ViewerListDto GetViewers(int channelId) => _presence.GetViewerList(channelId);

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _presence.Leave(Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }

        // ── Chat ───────────────────────────────────────────────────────────

        // content puede ser un mensaje, "/me ..." o un comando ("/ban usuario", "/poll ...").
        [Authorize]
        public async Task<ChatSendResult> SendMessage(int channelId, string content, string? replyToId)
        {
            var result = await _chat.SendAsync(CurrentSender()!, channelId, content ?? string.Empty, replyToId, Context.ConnectionAborted);
            await _dispatcher.DispatchAsync(channelId, result.Effects);
            return result;
        }

        public async Task<UserCardDto?> GetUserCard(int channelId, string userId)
            => await _chat.GetUserCardAsync(channelId, userId, CurrentSender());

        [Authorize]
        public Task<ChatActionResult> VotePoll(int channelId, string pollId, int optionIndex)
            => Run(channelId, _chat.VotePollAsync(channelId, pollId, optionIndex, CurrentSender()!));

        // ── Moderación ─────────────────────────────────────────────────────

        [Authorize]
        public Task<ChatActionResult> DeleteMessage(int channelId, string messageId)
            => Run(channelId, _chat.DeleteMessageAsync(channelId, messageId, CurrentSender()!));

        [Authorize]
        public Task<ChatActionResult> TimeoutUser(int channelId, string userId, int seconds)
            => Run(channelId, _chat.TimeoutAsync(channelId, userId, seconds, null, CurrentSender()!));

        [Authorize]
        public Task<ChatActionResult> BanUser(int channelId, string userId, string? reason)
            => Run(channelId, _chat.BanAsync(channelId, userId, reason, CurrentSender()!));

        [Authorize]
        public Task<ChatActionResult> UnbanUser(int channelId, string userId)
            => Run(channelId, _chat.UnbanAsync(channelId, userId, CurrentSender()!));

        [Authorize]
        public Task<ChatActionResult> ClearChat(int channelId)
            => Run(channelId, _chat.ClearAsync(channelId, CurrentSender()!));

        // role: "moderator", "vip" o "none"
        [Authorize]
        public Task<ChatActionResult> SetUserRole(int channelId, string userId, string role)
        {
            var parsed = role?.ToLowerInvariant() switch
            {
                "moderator" => ChannelRoleType.Moderator,
                "vip" => ChannelRoleType.Vip,
                _ => ChannelRoleType.None
            };
            return Run(channelId, _chat.SetRoleAsync(channelId, userId, parsed, CurrentSender()!));
        }

        [Authorize]
        public Task<ChatActionResult> PinMessage(int channelId, string messageId)
            => Run(channelId, _chat.PinAsync(channelId, messageId, CurrentSender()!));

        [Authorize]
        public Task<ChatActionResult> UnpinMessage(int channelId)
            => Run(channelId, _chat.UnpinAsync(channelId, CurrentSender()!));

        [Authorize]
        public Task<ChatActionResult> ResolveAutoMod(int channelId, string heldId, bool approve)
            => Run(channelId, _chat.ResolveAutoModAsync(channelId, heldId, approve, CurrentSender()!));

        private async Task<ChatActionResult> Run(int channelId, Task<ChatActionResult> action)
        {
            var result = await action;
            await _dispatcher.DispatchAsync(channelId, result.Effects);
            return result;
        }

        private ChatSender? CurrentSender()
        {
            var userId = Context.UserIdentifier;
            if (string.IsNullOrEmpty(userId)) return null;
            return new ChatSender(userId, Context.User?.IsInRole("Admin") == true);
        }
    }

    public record JoinChannelResult(
        int ViewerCount,
        bool CanModerate,
        string Role,
        ChatSettingsDto Settings,
        ChatMessageDto? Pinned,
        PollDto? Poll,
        IReadOnlyList<AutoModHeldDto> Held,
        int Points);
}
