using System.Collections.Concurrent;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common
{
    // "Base de datos" en memoria que comparten todos los dobles de prueba:
    // lo que en la app real está repartido entre SQL Server (usuarios, canales, follows,
    // bans) y MongoDB (chat, filtros, métricas, sesiones, notificaciones).
    //
    // Cada prueba crea la suya, así que las pruebas no se pisan entre sí.
    public sealed class InMemoryPlatform
    {
        public InMemoryPlatform(ManualTimeProvider? clock = null)
        {
            Clock = clock ?? new ManualTimeProvider();
        }

        public ManualTimeProvider Clock { get; }

        // ── SQL Server ──────────────────────────────────────────────────────
        public ConcurrentDictionary<string, AppUser> Users { get; } = new();
        public ConcurrentDictionary<int, ChannelStreamInfo> Channels { get; } = new();
        public ConcurrentDictionary<int, List<Emote>> Emotes { get; } = new();
        public ConcurrentDictionary<(string FollowerId, string FollowedId), DateTime> Follows { get; } = new();
        public ConcurrentDictionary<(int ChannelId, string UserId), BannedUser> Bans { get; } = new();
        public ConcurrentDictionary<int, int> PersistedViewerCounts { get; } = new();
        public ConcurrentDictionary<(int ChannelId, string UserId), ChannelRoleType> Roles { get; } = new();
        public ConcurrentDictionary<string, int> Points { get; } = new();

        // ── MongoDB ─────────────────────────────────────────────────────────
        public List<ChatMessage> Messages { get; } = new(); // usar con lock (Messages)
        public ConcurrentDictionary<string, ChatFilter> Filters { get; } = new();
        public ConcurrentDictionary<string, LiveMetric> Metrics { get; } = new();
        public List<StreamSession> Sessions { get; } = new(); // usar con lock (Sessions)
        public ConcurrentQueue<LiveNotification> LiveNotifications { get; } = new();
        public ConcurrentDictionary<string, string?> ChatColors { get; } = new();
        public ConcurrentQueue<WhisperMessage> Whispers { get; } = new();

        // ── Servicios externos ──────────────────────────────────────────────
        // AutoMod (Ollama o el servicio Python en la app real): la prueba decide qué responde.
        public ScriptedAutoModClassifier AutoMod { get; } = new();

        // Fase 4: lo que se le habría mandado al servicio Python para que AutoMod aprenda.
        public RecordingAutoModFeedbackSink AutoModFeedback { get; } = new();

        // ── Fallas simuladas (Mongo caído, etc.) ────────────────────────────
        public bool FailChatSaves { get; set; }
        public bool FailChatFilterReads { get; set; }
        public bool FailLiveMetrics { get; set; }

        public sealed record LiveNotification(IReadOnlyList<string> FollowerIds, string ChannelId, string ChannelName);

        // ════════════════════════════════════════════════════════════════════
        //  Armado de datos
        // ════════════════════════════════════════════════════════════════════

        public AppUser AddUser(string id, string displayName, bool suspended = false)
        {
            var user = new AppUser
            {
                Id = id,
                UserName = $"{id}@correo.test",
                Email = $"{id}@correo.test",
                DisplayUsername = displayName,
                // Suspendido = bloqueado hasta dentro de 10 años (igual que Admin → Banear).
                LockoutEnd = suspended ? DateTimeOffset.UtcNow.AddYears(10) : null
            };
            Users[id] = user;
            return user;
        }

        public ChannelStreamInfo AddChannel(int id, string ownerId, string name, string streamKey, string? category = "Charlando")
        {
            var owner = Users[ownerId];
            var channel = new ChannelStreamInfo
            {
                Id = id,
                UserId = ownerId,
                Name = name,
                StreamKey = streamKey,
                OwnerDisplayName = owner.DisplayUsername,
                OwnerLockoutEnd = owner.LockoutEnd,
                CategoryName = category
            };
            Channels[id] = channel;
            return channel;
        }

        public void AddEmote(int channelId, string code, string imageUrl)
        {
            var list = Emotes.GetOrAdd(channelId, _ => new List<Emote>());
            lock (list) list.Add(new Emote { Id = list.Count + 1, ChannelId = channelId, Code = code, ImageUrl = imageUrl });
        }

        public void Follow(string followerId, string followedId) =>
            Follows[(followerId, followedId)] = Clock.GetUtcNow().UtcDateTime;

        // Moderador o VIP del canal (Fase 2). ChannelRoleType.None quita el rol.
        public void SetRole(int channelId, string userId, ChannelRoleType role)
        {
            if (role == ChannelRoleType.None) Roles.TryRemove((channelId, userId), out _);
            else Roles[(channelId, userId)] = role;
        }

        public ChatFilter FilterFor(int channelId) =>
            Filters.GetOrAdd(channelId.ToString(), key => new ChatFilter { ChannelId = key });

        public List<ChatMessage> MessagesOf(int channelId, bool includeDeleted = false)
        {
            lock (Messages)
            {
                return Messages
                    .Where(m => m.ChannelId == channelId.ToString() && (includeDeleted || !m.IsDeleted))
                    .ToList();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Datos de ejemplo que usan casi todas las pruebas
        // ════════════════════════════════════════════════════════════════════

        public static InMemoryPlatform WithDemoData(ManualTimeProvider? clock = null)
        {
            var p = new InMemoryPlatform(clock);

            p.AddUser(Demo.Owner1, Demo.Owner1Name);
            p.AddUser(Demo.Owner2, Demo.Owner2Name);
            p.AddUser(Demo.Viewer1, Demo.Viewer1Name);
            p.AddUser(Demo.Viewer2, Demo.Viewer2Name);
            p.AddUser(Demo.Viewer3, Demo.Viewer3Name);
            p.AddUser(Demo.Admin, Demo.AdminName);
            p.AddUser(Demo.Suspended, Demo.SuspendedName, suspended: true);

            p.AddChannel(Demo.Channel1, Demo.Owner1, "Canal Uno", Demo.Channel1Key);
            p.AddChannel(Demo.Channel2, Demo.Owner2, "Canal Dos", Demo.Channel2Key, category: "Videojuegos");
            p.AddChannel(Demo.SuspendedChannel, Demo.Suspended, "Canal Suspendido", Demo.SuspendedChannelKey);

            p.AddEmote(Demo.Channel1, "pepega", "/emotes/1_pepega.png");
            p.AddEmote(Demo.Channel1, ":kappa:", "/emotes/1_kappa.png");

            // ViewerDos sigue al canal 1 (para las pruebas de "solo seguidores").
            p.Follow(Demo.Viewer2, Demo.Owner1);
            return p;
        }
    }

    // Identificadores de los datos de ejemplo, para que las pruebas se lean solas.
    public static class Demo
    {
        public const string Owner1 = "owner1";
        public const string Owner1Name = "StreamerUno";
        public const string Owner2 = "owner2";
        public const string Owner2Name = "StreamerDos";
        public const string Viewer1 = "u1";
        public const string Viewer1Name = "ViewerUno";
        public const string Viewer2 = "u2";
        public const string Viewer2Name = "ViewerDos";
        public const string Viewer3 = "u3";
        public const string Viewer3Name = "ViewerTres";
        public const string Admin = "admin";
        public const string AdminName = "ElAdmin";
        public const string Suspended = "suspendido";
        public const string SuspendedName = "Suspendido";

        public const int Channel1 = 1;
        public const int Channel2 = 2;
        public const int SuspendedChannel = 3;
        public const int MissingChannel = 999;

        // Claves de transmisión FICTICIAS de la plataforma en memoria (no existen fuera de las pruebas).
        public const string Channel1Key = "sk_ClaveDelCanalUno123"; // gitleaks:allow
        public const string Channel2Key = "sk_ClaveDelCanalDos456"; // gitleaks:allow
        public const string SuspendedChannelKey = "sk_ClaveDelSuspendido789"; // gitleaks:allow
    }
}
