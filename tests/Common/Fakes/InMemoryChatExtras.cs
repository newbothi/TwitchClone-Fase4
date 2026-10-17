using System.Collections.Concurrent;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes
{
    // Dobles de los servicios que agregó la Fase 2: roles del canal, búsqueda de usuarios,
    // puntos, color del chat, susurros, AutoMod y conteo de espectadores.

    public sealed class InMemoryChannelRoleRepository : IChannelRoleRepository
    {
        private readonly InMemoryPlatform _db;

        public InMemoryChannelRoleRepository(InMemoryPlatform db) => _db = db;

        public Task<Dictionary<string, ChannelRoleType>> GetRolesAsync(int channelId) =>
            Task.FromResult(_db.Roles.Where(kv => kv.Key.ChannelId == channelId).ToDictionary(kv => kv.Key.UserId, kv => kv.Value));

        public Task<List<ChannelRoleEntry>> ListAsync(int channelId) =>
            Task.FromResult(_db.Roles.Where(kv => kv.Key.ChannelId == channelId)
                .Select(kv => new ChannelRoleEntry
                {
                    ChannelId = channelId,
                    UserId = kv.Key.UserId,
                    DisplayName = _db.Users.TryGetValue(kv.Key.UserId, out var u) ? u.DisplayUsername : kv.Key.UserId,
                    Role = kv.Value,
                    CreatedAt = _db.Clock.GetUtcNow().UtcDateTime
                })
                .OrderBy(e => e.DisplayName)
                .ToList());

        public Task SetRoleAsync(int channelId, string userId, ChannelRoleType role, string grantedById)
        {
            _db.SetRole(channelId, userId, role);
            return Task.CompletedTask;
        }
    }

    public sealed class InMemoryChatUserRepository : IChatUserRepository
    {
        private readonly InMemoryPlatform _db;

        public InMemoryChatUserRepository(InMemoryPlatform db) => _db = db;

        public Task<ChatUserLookup?> FindByDisplayNameAsync(string displayName)
        {
            var user = _db.Users.Values.FirstOrDefault(u => string.Equals(u.DisplayUsername, displayName, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(user == null ? null : new ChatUserLookup { Id = user.Id, DisplayUsername = user.DisplayUsername });
        }

        public Task<DateTime?> GetFollowDateAsync(string followerId, string followedId) =>
            Task.FromResult<DateTime?>(_db.Follows.TryGetValue((followerId, followedId), out var since) ? since : null);

        public Task<int> GetPointsAsync(string userId) => Task.FromResult(_db.Points.GetValueOrDefault(userId));

        public Task AddPointsAsync(IReadOnlyCollection<string> userIds, int amount)
        {
            foreach (var id in userIds) _db.Points.AddOrUpdate(id, amount, (_, current) => current + amount);
            return Task.CompletedTask;
        }

        public Task<bool> TryDonatePointsAsync(string fromUserId, int channelId, int amount, string message)
        {
            lock (_db.Points)
            {
                var current = _db.Points.GetValueOrDefault(fromUserId);
                if (current < amount) return Task.FromResult(false);
                _db.Points[fromUserId] = current - amount;
                return Task.FromResult(true);
            }
        }
    }

    public sealed class InMemoryChatUserSettingsService : IChatUserSettingsService
    {
        private readonly InMemoryPlatform _db;

        public InMemoryChatUserSettingsService(InMemoryPlatform db) => _db = db;

        public Task<string?> GetColorAsync(string userId) => Task.FromResult(_db.ChatColors.GetValueOrDefault(userId));

        public Task SetColorAsync(string userId, string? color)
        {
            _db.ChatColors[userId] = color;
            return Task.CompletedTask;
        }
    }

    public sealed class InMemoryWhisperService : IWhisperService
    {
        private readonly InMemoryPlatform _db;

        public InMemoryWhisperService(InMemoryPlatform db) => _db = db;

        public Task<WhisperMessage> SendAsync(string senderId, string senderUserName, string recipientId, string recipientUserName, string content)
        {
            var whisper = new WhisperMessage
            {
                Id = Guid.NewGuid().ToString("N")[..24],
                ConversationId = string.Join("_", new[] { senderId, recipientId }.OrderBy(x => x, StringComparer.Ordinal)),
                SenderId = senderId,
                SenderUserName = senderUserName,
                RecipientId = recipientId,
                RecipientUserName = recipientUserName,
                Content = content
            };
            _db.Whispers.Enqueue(whisper);
            return Task.FromResult(whisper);
        }

        public Task<List<WhisperMessage>> GetConversationAsync(string conversationId, int limit = 100) =>
            Task.FromResult(_db.Whispers.Where(w => w.ConversationId == conversationId).TakeLast(limit).ToList());

        public Task<List<WhisperConversationSummary>> GetConversationsForUserAsync(string userId) => throw new NotSupportedException();
        public Task MarkConversationAsReadAsync(string conversationId, string readerId) => Task.CompletedTask;
        public Task<int> GetUnreadCountAsync(string userId) => Task.FromResult(0);
    }

    // AutoMod de prueba: la prueba decide qué responde. Por defecto, todo limpio.
    public sealed class ScriptedAutoModClassifier : IAutoModClassifier
    {
        private readonly ConcurrentQueue<string> _received = new();

        public bool IsAvailable { get; set; } = true;
        public string ProviderName => "Pruebas";

        // Texto → veredicto. Si devuelve null, el mensaje está limpio.
        public Func<string, AutoModVerdict?> Rule { get; set; } = _ => null;

        // Simula un servicio lento o caído.
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public bool Throw { get; set; }

        public IReadOnlyCollection<string> Received => _received.ToArray();

        public async Task<AutoModVerdict> ClassifyAsync(string text, CancellationToken ct)
        {
            _received.Enqueue(text);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (Throw) throw new HttpRequestException("AutoMod no disponible (simulado).");
            return Rule(text) ?? AutoModVerdict.Clean;
        }

        // Atajo: marca los mensajes que contienen alguna de estas palabras.
        public ScriptedAutoModClassifier FlagWhenContains(string category, params string[] words)
        {
            Rule = text => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))
                ? new AutoModVerdict(true, category, "regla de prueba")
                : null;
            return this;
        }
    }

    // Conteo de espectadores fijo para las pruebas unitarias del chat (/raid usa el conteo).
    public sealed class FixedPresenceCounter : IPresenceCounter
    {
        public ConcurrentDictionary<int, HashSet<string>> UsersByChannel { get; } = new();

        public int GetCount(int channelId) => UsersByChannel.TryGetValue(channelId, out var users) ? users.Count : 0;

        public IReadOnlyCollection<string> GetUserIds(int channelId) =>
            UsersByChannel.TryGetValue(channelId, out var users) ? users.ToList() : Array.Empty<string>();
    }
}
