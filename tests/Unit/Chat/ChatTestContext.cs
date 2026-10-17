using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Chat
{
    // Arma un ChatService REAL sobre la plataforma en memoria y el reloj manual.
    internal sealed class ChatTestContext
    {
        public ChatTestContext()
        {
            Db = InMemoryPlatform.WithDemoData();
            Chat = CreateService();
        }

        public InMemoryPlatform Db { get; }
        public ManualTimeProvider Clock => Db.Clock;
        public ChatService Chat { get; }
        public ScriptedAutoModClassifier AutoMod => Db.AutoMod;
        public FixedPresenceCounter Presence { get; } = new();
        public AutoModQueue AutoModQueue { get; } = new();
        public AutoModSettings AutoModSettings { get; } = new() { TimeoutMs = 300 };

        // Un servicio nuevo tiene su propia caché (como si la app se hubiera reiniciado).
        public ChatService CreateService() => new(
            new InMemoryChannelRepository(Db),
            new InMemoryUserRepository(Db),
            new InMemoryChatUserRepository(Db),
            new InMemoryChannelRoleRepository(Db),
            new InMemoryFollowService(Db),
            new InMemoryModerationService(Db),
            new InMemoryChatMessageService(Db),
            new InMemoryChatFilterService(Db),
            new InMemoryChatUserSettingsService(Db),
            new InMemoryWhisperService(Db),
            Db.AutoMod,
            new PollService(Db.Clock),
            Presence,
            new RecentMessageCache(),
            AutoModQueue,
            new MemoryCache(new MemoryCacheOptions()),
            Db.Clock,
            Options.Create(AutoModSettings),
            NullLogger<ChatService>.Instance);

        public Task<ChatSendResult> Send(ChatSender sender, string text, int channelId = Demo.Channel1, string? replyToId = null) =>
            Chat.SendAsync(sender, channelId, text, replyToId);

        // Configura el canal 1 antes de usar el chat (la configuración se cachea 30 s).
        // Ojo: en la Fase 2 los enlaces vienen bloqueados por defecto (como en Twitch).
        public void ConfigureChannel1(int slowModeSeconds = 0, bool followersOnly = false, params string[] bannedWords) =>
            ConfigureChannel(Demo.Channel1, f =>
            {
                f.SlowModeEnabled = slowModeSeconds > 0;
                f.SlowModeSeconds = slowModeSeconds;
                f.FollowersOnlyMode = followersOnly;
                f.BannedWords = bannedWords.ToList();
            });

        public void ConfigureChannel(int channelId, Action<ChatFilter> change) => change(Db.FilterFor(channelId));

        public void MakeModerator(string userId, int channelId = Demo.Channel1) => Db.SetRole(channelId, userId, ChannelRoleType.Moderator);
        public void MakeVip(string userId, int channelId = Demo.Channel1) => Db.SetRole(channelId, userId, ChannelRoleType.Vip);
    }

    // Quién escribe. IsAdmin = rol "Admin" de la plataforma.
    internal static class As
    {
        public static readonly ChatSender Owner = new(Demo.Owner1, IsAdmin: false);
        public static readonly ChatSender OtherOwner = new(Demo.Owner2, IsAdmin: false);
        public static readonly ChatSender Viewer1 = new(Demo.Viewer1, IsAdmin: false);
        public static readonly ChatSender Viewer2 = new(Demo.Viewer2, IsAdmin: false);
        public static readonly ChatSender Viewer3 = new(Demo.Viewer3, IsAdmin: false);
        public static readonly ChatSender Admin = new(Demo.Admin, IsAdmin: true);
        public static readonly ChatSender Suspended = new(Demo.Suspended, IsAdmin: false);
        public static readonly ChatSender Ghost = new("no-existe", IsAdmin: false);
    }
}
