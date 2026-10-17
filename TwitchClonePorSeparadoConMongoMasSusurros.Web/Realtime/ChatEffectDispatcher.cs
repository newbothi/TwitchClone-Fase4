using Microsoft.AspNetCore.SignalR;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime
{
    // Algo que quiere enterarse de lo que pasa en el chat sin ser un navegador
    // (por ejemplo, el simulador de bots: reacciona a mensajes, encuestas y raids).
    public interface IChatEventObserver
    {
        Task OnEffectsAsync(int channelId, IReadOnlyList<ChatEffect> effects);
    }

    // Traduce los ChatEffect que devuelve IChatService a eventos de SignalR.
    // Lo usan el hub, los controladores (Dashboard) y el simulador, así que todos
    // difunden exactamente igual.
    //
    // Eventos hacia el navegador:
    //  Canal:     ChatMessage · MessageDeleted · UserTimedOut · UserBanned · ChatCleared ·
    //             ChatSettingsChanged · UserRoleChanged · PinChanged · PollUpdated ·
    //             RaidStarted · RaidIncoming
    //  Mods:      AutoModHeld · AutoModResolved
    //  Usuario:   TimedOut · Banned · Unbanned · RoleChanged · ChatNotice · WhisperReceived
    public sealed class ChatEffectDispatcher
    {
        public const int RaidCountdownSeconds = 10;

        private readonly IHubContext<StreamHub> _hub;
        private readonly Lazy<IChatEventObserver[]> _observers;
        private readonly ILogger<ChatEffectDispatcher> _logger;

        // Los observadores se resuelven tarde: el simulador observa el chat y a la vez
        // usa este dispatcher para publicar los mensajes de sus bots.
        public ChatEffectDispatcher(IHubContext<StreamHub> hub, IServiceProvider services, ILogger<ChatEffectDispatcher> logger)
        {
            _hub = hub;
            _observers = new Lazy<IChatEventObserver[]>(() => services.GetServices<IChatEventObserver>().ToArray());
            _logger = logger;
        }

        public static string RoleName(ChannelRoleType role) => role switch
        {
            ChannelRoleType.Moderator => "moderator",
            ChannelRoleType.Vip => "vip",
            _ => "none"
        };

        public async Task DispatchAsync(int channelId, IReadOnlyList<ChatEffect> effects)
        {
            if (effects.Count == 0) return;

            var channel = _hub.Clients.Group(StreamHub.ChannelGroup(channelId));
            var mods = _hub.Clients.Group(StreamHub.ModsGroup(channelId));

            foreach (var effect in effects)
            {
                switch (effect)
                {
                    case BroadcastMessageEffect e:
                        await channel.SendAsync("ChatMessage", e.Message);
                        break;

                    case MessageDeletedEffect e:
                        await channel.SendAsync("MessageDeleted", e.MessageId);
                        break;

                    case UserSanctionedEffect e when e.IsBan:
                        await channel.SendAsync("UserBanned", e.UserId, e.DisplayName);
                        await _hub.Clients.User(e.UserId).SendAsync("Banned", channelId);
                        break;

                    case UserSanctionedEffect e:
                        await channel.SendAsync("UserTimedOut", e.UserId, e.DisplayName, e.Seconds);
                        await _hub.Clients.User(e.UserId).SendAsync("TimedOut", channelId, e.Seconds);
                        break;

                    case UserUnbannedEffect e:
                        await _hub.Clients.User(e.UserId).SendAsync("Unbanned", channelId);
                        break;

                    case ChatClearedEffect:
                        await channel.SendAsync("ChatCleared");
                        break;

                    case SettingsChangedEffect e:
                        await channel.SendAsync("ChatSettingsChanged", e.Settings);
                        break;

                    case RoleChangedEffect e:
                        await channel.SendAsync("UserRoleChanged", e.UserId, e.DisplayName, RoleName(e.Role));
                        await _hub.Clients.User(e.UserId).SendAsync("RoleChanged", channelId, RoleName(e.Role));
                        break;

                    case PinChangedEffect e:
                        await channel.SendAsync("PinChanged", e.Message);
                        break;

                    case AutoModHeldEffect e:
                        await mods.SendAsync("AutoModHeld", e.Held);
                        break;

                    case AutoModResolvedEffect e:
                        await mods.SendAsync("AutoModResolved", e.HeldId, e.Approved, e.ModeratorName);
                        break;

                    case PollEffect e:
                        await channel.SendAsync("PollUpdated", e.Poll);
                        break;

                    case RaidEffect e:
                        await channel.SendAsync("RaidStarted", e.TargetChannelId, e.TargetName, e.Viewers, RaidCountdownSeconds);
                        await _hub.Clients.Group(StreamHub.ChannelGroup(e.TargetChannelId)).SendAsync("RaidIncoming", e.SourceName, e.Viewers);
                        break;

                    case WhisperEffect e:
                        await _hub.Clients.User(e.RecipientId).SendAsync("WhisperReceived", e.ConversationId, e.SenderName, e.Content);
                        break;

                    case UserNoticeEffect e:
                        await _hub.Clients.User(e.UserId).SendAsync("ChatNotice", channelId, e.Text);
                        break;
                }
            }

            foreach (var observer in _observers.Value)
            {
                try
                {
                    await observer.OnEffectsAsync(channelId, effects);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Un observador del chat falló procesando efectos del canal {ChannelId}.", channelId);
                }
            }
        }
    }
}
