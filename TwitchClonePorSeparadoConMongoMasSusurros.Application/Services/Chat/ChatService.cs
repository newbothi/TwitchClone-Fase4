using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat
{
    // Pipeline del chat en vivo. Todo lo que decide si un mensaje sale o no vive aquí,
    // nunca en el navegador: el cliente solo manda (channelId, texto, respuestaA).
    //
    // Está dividido en tres archivos:
    //   ChatService.cs             → envío de mensajes, historial, configuración, cachés
    //   ChatService.Moderation.cs  → bans, timeouts, roles, fijados, AutoMod, encuestas, tarjeta
    //   ChatService.Commands.cs    → comandos de chat (/ban, /slow, /poll, /raid, /w, ...)
    //
    // Estado en memoria (modo lento, límites, cachés): sirve para UNA instancia de la app.
    public partial class ChatService : IChatService
    {
        public const int MaxMessageLength = 500;

        private const int RateLimitMessages = 20;              // viewers: 20 mensajes / 30 s
        private const int PrivilegedRateLimitMessages = 100;   // mods, VIP, dueño
        private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(30);

        private const int MaxTimeoutSeconds = 14 * 24 * 60 * 60; // 14 días, como Twitch
        private const int MaxSlowModeSeconds = 600;

        private static readonly TimeSpan ChannelContextTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan UserProfileTtl = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ColorTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan BanTtl = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan FollowPositiveTtl = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan FollowNegativeTtl = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ThrottleStateTtl = TimeSpan.FromMinutes(5);

        private readonly IChannelRepository _channels;
        private readonly IUserRepository _users;
        private readonly IChatUserRepository _chatUsers;
        private readonly IChannelRoleRepository _roles;
        private readonly IFollowService _follows;
        private readonly IModerationService _moderation;
        private readonly IChatMessageService _messages;
        private readonly IChatFilterService _filters;
        private readonly IChatUserSettingsService _userSettings;
        private readonly IWhisperService _whispers;
        private readonly IAutoModClassifier _classifier;
        private readonly IPollService _polls;
        private readonly IPresenceCounter _presence;
        private readonly RecentMessageCache _recent;
        private readonly AutoModQueue _autoModQueue;
        private readonly IMemoryCache _cache;
        private readonly TimeProvider _time;
        private readonly AutoModSettings _autoModSettings;
        private readonly ILogger<ChatService> _logger;

        public ChatService(
            IChannelRepository channels,
            IUserRepository users,
            IChatUserRepository chatUsers,
            IChannelRoleRepository roles,
            IFollowService follows,
            IModerationService moderation,
            IChatMessageService messages,
            IChatFilterService filters,
            IChatUserSettingsService userSettings,
            IWhisperService whispers,
            IAutoModClassifier classifier,
            IPollService polls,
            IPresenceCounter presence,
            RecentMessageCache recent,
            AutoModQueue autoModQueue,
            IMemoryCache cache,
            TimeProvider time,
            IOptions<AutoModSettings> autoModSettings,
            ILogger<ChatService> logger)
        {
            _channels = channels;
            _users = users;
            _chatUsers = chatUsers;
            _roles = roles;
            _follows = follows;
            _moderation = moderation;
            _messages = messages;
            _filters = filters;
            _userSettings = userSettings;
            _whispers = whispers;
            _classifier = classifier;
            _polls = polls;
            _presence = presence;
            _recent = recent;
            _autoModQueue = autoModQueue;
            _cache = cache;
            _time = time;
            _autoModSettings = autoModSettings.Value;
            _logger = logger;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Envío de mensajes
        // ════════════════════════════════════════════════════════════════════

        public async Task<ChatSendResult> SendAsync(ChatSender sender, int channelId, string content, string? replyToId = null, CancellationToken ct = default)
        {
            var channel = await GetChannelContextAsync(channelId);
            if (channel == null)
                return ChatSendResult.Fail("channel_not_found", "Este canal no existe.");

            var profile = await GetUserProfileAsync(sender.UserId);
            if (profile == null)
                return ChatSendResult.Fail("user_not_found", "No encontramos tu cuenta. Vuelve a iniciar sesión.");
            if (profile.IsSuspended)
                return ChatSendResult.Fail("suspended", "Tu cuenta está suspendida.");

            var actor = BuildActor(channel, sender);

            // Comandos (/me es un mensaje normal con formato de "acción").
            var isAction = false;
            var trimmed = (content ?? string.Empty).TrimStart();
            if (trimmed.StartsWith('/'))
            {
                if (trimmed.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
                {
                    isAction = true;
                    content = trimmed[4..];
                }
                else
                {
                    return ChatSendResult.FromAction(await ExecuteCommandAsync(channel, actor, profile, trimmed, ct));
                }
            }

            content = Normalize(content);
            if (content.Length == 0)
                return ChatSendResult.Fail("empty", "El mensaje está vacío.");
            if (content.Length > MaxMessageLength)
                return ChatSendResult.Fail("too_long", $"Tu mensaje supera los {MaxMessageLength} caracteres.");

            // 1) Ban / timeout del canal
            if (!actor.IsBroadcaster)
            {
                var ban = await GetBanSnapshotAsync(channelId, sender.UserId);
                if (ban != null)
                {
                    if (ban.ExpiresAtUtc == null)
                        return ChatSendResult.Fail("banned", "Estás baneado de este chat.");

                    var remaining = (int)Math.Ceiling((ban.ExpiresAtUtc.Value - UtcNow()).TotalSeconds);
                    if (remaining > 0)
                        return ChatSendResult.Fail("timed_out", $"Estás silenciado. Podrás escribir en {ChatText.FormatDuration(remaining)}.", remaining);
                }
            }

            // 2) Solo seguidores (mods y VIP no se ven afectados)
            if (!actor.IsModerator && !actor.IsVip && channel.Filter.FollowersOnlyMode
                && !await IsFollowingCachedAsync(channel, sender.UserId))
                return ChatSendResult.Fail("followers_only", "Este chat está en modo solo seguidores. Sigue el canal para escribir.");

            var fragments = EmoteParser.Parse(content, channel.Emotes);

            // 3) Solo emotes
            if (!actor.IsModerator && channel.Filter.EmoteOnlyMode
                && fragments.Any(f => f.Type == ChatFragment.TextType && !string.IsNullOrWhiteSpace(f.Text)))
                return ChatSendResult.Fail("emote_only", "Este chat está en modo solo emotes.");

            // 4) Enlaces (VIP y mods sí pueden)
            if (!actor.IsModerator && !actor.IsVip && channel.Filter.BlockLinks && ChatText.ContainsLink(content))
                return ChatSendResult.Fail("links_blocked", "En este chat no se permiten enlaces.");

            // 5) Palabras bloqueadas (el dueño y los mods no se filtran)
            if (!actor.IsModerator && ContainsBlockedTerm(content, channel.Filter.BannedWords))
                return ChatSendResult.Fail("blocked_term", "Tu mensaje contiene una palabra bloqueada en este canal.");

            // 6) Mayúsculas y caracteres repetidos (Fase 4; VIP y mods no se filtran).
            //    Son las reglas del score_heuristic del microservicio Python original.
            if (!actor.IsModerator && !actor.IsVip)
            {
                if (channel.Filter.BlockExcessiveCaps && ChatText.HasExcessiveCaps(fragments))
                    return ChatSendResult.Fail("too_many_caps", "Tu mensaje tiene demasiadas mayúsculas. Escríbelo en minúsculas.");
                if (channel.Filter.BlockRepeatedChars && ChatText.HasRepeatedChars(fragments))
                    return ChatSendResult.Fail("repeated_chars", $"Tu mensaje repite el mismo carácter {ChatText.RepeatedCharsLimit} o más veces seguidas.");
            }

            // 7) Repetidos + modo lento + límite de mensajes, atómico por usuario
            var now = UtcNow();
            var throttle = _cache.GetOrCreate($"chat:throttle:{channelId}:{sender.UserId}", e =>
            {
                e.SlidingExpiration = ThrottleStateTtl;
                return new UserThrottleState();
            })!;

            lock (throttle)
            {
                var normalizedText = content.ToLowerInvariant();
                if (!actor.IsModerator && throttle.LastText == normalizedText && now - throttle.LastAcceptedUtc < DuplicateWindow)
                    return ChatSendResult.Fail("duplicate", "Tu mensaje es idéntico al anterior. Espera unos segundos o cámbialo un poco.");

                var slowSeconds = channel.Filter.SlowModeEnabled ? channel.Filter.SlowModeSeconds : 0;
                if (!actor.IsModerator && !actor.IsVip && slowSeconds > 0 && throttle.LastAcceptedUtc != DateTime.MinValue)
                {
                    var wait = (int)Math.Ceiling((throttle.LastAcceptedUtc.AddSeconds(slowSeconds) - now).TotalSeconds);
                    if (wait > 0)
                        return ChatSendResult.Fail("slow_mode", $"Modo lento activo: espera {wait} s para volver a escribir.", wait);
                }

                while (throttle.Recent.Count > 0 && now - throttle.Recent.Peek() >= RateLimitWindow)
                    throttle.Recent.Dequeue();

                var limit = actor.IsModerator || actor.IsVip ? PrivilegedRateLimitMessages : RateLimitMessages;
                if (throttle.Recent.Count >= limit)
                {
                    var retry = (int)Math.Ceiling((throttle.Recent.Peek() + RateLimitWindow - now).TotalSeconds);
                    return ChatSendResult.Fail("rate_limited", "Estás enviando mensajes demasiado rápido. Espera unos segundos.", Math.Max(retry, 1));
                }

                throttle.Recent.Enqueue(now);
                throttle.LastAcceptedUtc = now;
                throttle.LastText = normalizedText;
            }

            var message = await BuildMessageAsync(channel, actor, profile, content, fragments, now, isAction, isAnnouncement: false, replyToId);

            // 8) AutoMod con IA (no aplica a mods, VIP ni al dueño)
            if (channel.Filter.AutoModEnabled && _classifier.IsAvailable && !actor.IsModerator && !actor.IsVip)
            {
                var verdict = await ClassifyAsync(content, ct);
                if (verdict.Flagged)
                {
                    var held = _autoModQueue.Hold(channelId, message, verdict.Category, verdict.Reason, now);
                    return new ChatSendResult
                    {
                        Ok = true,
                        HeldForReview = true,
                        Notice = "AutoMod retuvo tu mensaje: un moderador lo revisará antes de publicarlo.",
                        Effects = { new AutoModHeldEffect(held) }
                    };
                }
            }

            var dto = await PublishAsync(message, channelId);
            return ChatSendResult.Success(dto);
        }

        public async Task<List<ChatMessageDto>> GetHistoryAsync(int channelId, int limit = 50)
        {
            await _messages.FlushAsync(); // incluir lo que aún esté en la cola de escritura
            var messages = await _messages.GetRecentMessagesAsync(channelId.ToString(), limit);
            return messages.Select(m => ToDto(m, channelId)).ToList();
        }

        // ════════════════════════════════════════════════════════════════════
        //  Configuración del chat
        // ════════════════════════════════════════════════════════════════════

        public async Task<ChatSettingsDto> GetSettingsAsync(int channelId)
        {
            var ctx = await GetChannelContextAsync(channelId);
            return ToPublicSettings(ctx?.Filter ?? new ChatFilter());
        }

        public async Task<ChatModerationSettingsDto> GetModerationSettingsAsync(int channelId)
        {
            var filter = await _filters.GetFiltersAsync(channelId.ToString());
            return new ChatModerationSettingsDto
            {
                SlowModeSeconds = filter.SlowModeEnabled ? filter.SlowModeSeconds : 0,
                FollowersOnly = filter.FollowersOnlyMode,
                EmoteOnly = filter.EmoteOnlyMode,
                Rules = filter.Rules ?? string.Empty,
                BannedWords = filter.BannedWords.OrderBy(w => w).ToList(),
                BlockLinks = filter.BlockLinks,
                AutoModEnabled = filter.AutoModEnabled,
                BlockExcessiveCaps = filter.BlockExcessiveCaps,
                BlockRepeatedChars = filter.BlockRepeatedChars
            };
        }

        public async Task<ChatActionResult> UpdateSettingsAsync(int channelId, ChatModerationSettingsDto settings)
        {
            var key = channelId.ToString();
            var slow = Math.Clamp(settings.SlowModeSeconds, 0, MaxSlowModeSeconds);

            await _filters.SetSlowModeAsync(key, slow > 0, slow);
            await _filters.SetFollowersOnlyAsync(key, settings.FollowersOnly);
            await _filters.SetEmoteOnlyAsync(key, settings.EmoteOnly);
            await _filters.SetBlockLinksAsync(key, settings.BlockLinks);
            await _filters.SetAutoModAsync(key, settings.AutoModEnabled);
            await _filters.SetBlockCapsAsync(key, settings.BlockExcessiveCaps);
            await _filters.SetBlockRepeatedCharsAsync(key, settings.BlockRepeatedChars);

            var rules = (settings.Rules ?? string.Empty).Trim();
            await _filters.SetRulesAsync(key, rules.Length > 1000 ? rules[..1000] : rules);

            var desired = settings.BannedWords
                .Select(w => w.Trim().ToLowerInvariant())
                .Where(w => w.Length is > 0 and <= 50)
                .Distinct()
                .Take(200)
                .ToHashSet();

            var current = (await _filters.GetFiltersAsync(key)).BannedWords.ToHashSet();
            foreach (var word in current.Except(desired))
                await _filters.RemoveBannedWordAsync(key, word);
            foreach (var word in desired.Except(current))
                await _filters.AddBannedWordAsync(key, word);

            InvalidateChannel(channelId);
            return ChatActionResult.Success("Configuración del chat guardada.",
                new SettingsChangedEffect(await GetSettingsAsync(channelId)));
        }

        // ════════════════════════════════════════════════════════════════════
        //  Datos cacheados
        // ════════════════════════════════════════════════════════════════════

        public async Task<string?> GetChannelOwnerIdAsync(int channelId)
            => (await GetChannelContextAsync(channelId))?.OwnerUserId;

        public async Task<ChatUserProfile?> GetUserProfileAsync(string userId)
        {
            var key = $"chat:user:{userId}";
            if (_cache.TryGetValue(key, out ChatUserProfile? cached)) return cached;

            var user = await _users.GetByIdAsync(userId);
            if (user == null) return null;

            // Ojo: UserName es el email. En el chat solo se muestra DisplayUsername.
            var displayName = string.IsNullOrWhiteSpace(user.DisplayUsername) ? "usuario" : user.DisplayUsername;
            var suspended = user.LockoutEnd.HasValue && user.LockoutEnd.Value > _time.GetUtcNow();

            var profile = new ChatUserProfile(user.Id, displayName, suspended, user.CreatedAt);
            _cache.Set(key, profile, UserProfileTtl);
            return profile;
        }

        public async Task<ChannelRoleType> GetChannelRoleAsync(int channelId, string userId)
        {
            var ctx = await GetChannelContextAsync(channelId);
            return ctx != null && ctx.Roles.TryGetValue(userId, out var role) ? role : ChannelRoleType.None;
        }

        public void InvalidateChannel(int channelId) => _cache.Remove($"chat:ctx:{channelId}");

        private void InvalidateBan(int channelId, string userId) => _cache.Remove($"chat:ban:{channelId}:{userId}");

        private async Task<ChannelChatContext?> GetChannelContextAsync(int channelId)
        {
            var key = $"chat:ctx:{channelId}";
            if (_cache.TryGetValue(key, out ChannelChatContext? cached)) return cached;

            var owner = await _channels.GetOwnerUserIdAsync(channelId);
            if (owner == null) return null;

            var emotes = EmoteParser.BuildLookup(await _channels.GetEmotesAsync(channelId));

            ChatFilter filter;
            try
            {
                filter = await _filters.GetFiltersAsync(channelId.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo leer la configuración del chat del canal {ChannelId} desde MongoDB; se usan valores por defecto.", channelId);
                filter = new ChatFilter { ChannelId = channelId.ToString() };
            }

            Dictionary<string, ChannelRoleType> roles;
            try
            {
                roles = await _roles.GetRolesAsync(channelId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudieron leer los moderadores/VIP del canal {ChannelId}.", channelId);
                roles = new Dictionary<string, ChannelRoleType>();
            }

            var ctx = new ChannelChatContext(channelId, owner, emotes, filter, roles);
            _cache.Set(key, ctx, ChannelContextTtl);
            return ctx;
        }

        private async Task<BanSnapshot?> GetBanSnapshotAsync(int channelId, string userId)
        {
            var key = $"chat:ban:{channelId}:{userId}";
            if (_cache.TryGetValue(key, out BanSnapshot? cached))
            {
                if (cached?.ExpiresAtUtc != null && cached.ExpiresAtUtc <= UtcNow()) return null;
                return cached;
            }

            var ban = await _moderation.GetActiveBanAsync(channelId, userId);
            var snapshot = ban == null ? null : new BanSnapshot(ban.ExpiresAt);
            _cache.Set(key, snapshot, BanTtl);
            return snapshot;
        }

        private async Task<bool> IsFollowingCachedAsync(ChannelChatContext channel, string userId)
        {
            var key = $"chat:follow:{channel.ChannelId}:{userId}";
            if (_cache.TryGetValue(key, out bool following)) return following;

            following = await _follows.IsFollowingAsync(userId, channel.OwnerUserId);
            _cache.Set(key, following, following ? FollowPositiveTtl : FollowNegativeTtl);
            return following;
        }

        private async Task<string> GetColorAsync(string userId)
        {
            var key = $"chat:color:{userId}";
            if (_cache.TryGetValue(key, out string? cached) && cached != null) return cached;

            string? custom = null;
            try
            {
                custom = await _userSettings.GetColorAsync(userId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No se pudo leer el color del usuario {UserId}.", userId);
            }

            var color = custom ?? ChatColors.ForUser(userId);
            _cache.Set(key, color, ColorTtl);
            return color;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Construcción y publicación de mensajes
        // ════════════════════════════════════════════════════════════════════

        private async Task<ChatMessage> BuildMessageAsync(ChannelChatContext channel, ChatActor actor, ChatUserProfile profile,
            string content, List<ChatFragment> fragments, DateTime now, bool isAction, bool isAnnouncement, string? replyToId)
        {
            return new ChatMessage
            {
                Id = ObjectId.GenerateNewId().ToString(),
                ChannelId = channel.ChannelId.ToString(),
                UserId = actor.Sender.UserId,
                UserName = profile.DisplayName,
                Content = content,
                SentAt = now,
                Color = await GetColorAsync(actor.Sender.UserId),
                Badges = actor.Badges(),
                Fragments = fragments,
                IsAction = isAction,
                IsAnnouncement = isAnnouncement,
                ReplyTo = await ResolveReplyAsync(channel.ChannelId, replyToId),
                IsSimulated = actor.Sender.IsSimulated
            };
        }

        // Guarda (en cola) y deja el mensaje en la caché de recientes.
        private async Task<ChatMessageDto> PublishAsync(ChatMessage message, int channelId)
        {
            try
            {
                await _messages.SaveMessageAsync(message);
            }
            catch (Exception ex)
            {
                // El chat en vivo no se cae porque Mongo tenga un problema: el mensaje
                // igual se difunde, solo no quedará en el historial.
                _logger.LogWarning(ex, "No se pudo guardar el mensaje de chat en MongoDB (canal {ChannelId}).", channelId);
            }

            var dto = ToDto(message, channelId);
            _recent.Add(dto);
            return dto;
        }

        private async Task<ChatReplyInfo?> ResolveReplyAsync(int channelId, string? replyToId)
        {
            if (string.IsNullOrWhiteSpace(replyToId)) return null;

            var parent = _recent.Find(channelId, replyToId);
            if (parent == null && ObjectId.TryParse(replyToId, out _))
            {
                var stored = await _messages.GetByIdAsync(replyToId);
                if (stored != null && stored.ChannelId == channelId.ToString() && !stored.IsDeleted)
                    parent = ToDto(stored, channelId);
            }
            if (parent == null) return null;

            var text = parent.PlainText();
            return new ChatReplyInfo
            {
                MessageId = parent.Id,
                UserId = parent.UserId,
                DisplayName = parent.DisplayName,
                Text = text.Length > 80 ? text[..80] + "…" : text
            };
        }

        private async Task<AutoModVerdict> ClassifyAsync(string content, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(200, _autoModSettings.TimeoutMs)));
            try
            {
                return await _classifier.ClassifyAsync(content, timeout.Token);
            }
            catch (Exception ex)
            {
                // Falla "abierto": si el modelo no responde, el chat no se congela.
                _logger.LogDebug(ex, "AutoMod ({Provider}) no respondió a tiempo; el mensaje pasa sin revisión.", _classifier.ProviderName);
                return AutoModVerdict.Clean;
            }
        }

        private static ChatMessageDto ToDto(ChatMessage m, int channelId) => new()
        {
            Id = m.Id,
            ChannelId = channelId,
            UserId = m.UserId,
            DisplayName = m.UserName,
            Color = string.IsNullOrEmpty(m.Color) ? ChatColors.ForUser(m.UserId ?? string.Empty) : m.Color,
            Badges = m.Badges ?? new List<string>(),
            // Mensajes guardados antes de la Fase 1 no tienen fragmentos: se muestran como texto.
            Fragments = m.Fragments is { Count: > 0 } ? m.Fragments : new List<ChatFragment> { ChatFragment.FromText(m.Content ?? string.Empty) },
            SentAt = DateTime.SpecifyKind(m.SentAt, DateTimeKind.Utc),
            IsAction = m.IsAction,
            IsAnnouncement = m.IsAnnouncement,
            ReplyTo = m.ReplyTo
        };

        private static ChatSettingsDto ToPublicSettings(ChatFilter filter) => new()
        {
            SlowModeSeconds = filter.SlowModeEnabled ? filter.SlowModeSeconds : 0,
            FollowersOnly = filter.FollowersOnlyMode,
            EmoteOnly = filter.EmoteOnlyMode,
            Rules = filter.Rules ?? string.Empty
        };

        private static string Normalize(string? content)
        {
            if (string.IsNullOrEmpty(content)) return string.Empty;
            var chars = content.Select(c => c is '\r' or '\n' or '\t' ? ' ' : c)
                               .Where(c => !char.IsControl(c))
                               .ToArray();
            return new string(chars).Trim();
        }

        private static bool ContainsBlockedTerm(string content, List<string> bannedWords)
        {
            if (bannedWords.Count == 0) return false;
            var lower = content.ToLowerInvariant();
            return bannedWords.Any(w => w.Length > 0 && lower.Contains(w, StringComparison.Ordinal));
        }

        public static string FormatDuration(int seconds) => ChatText.FormatDuration(seconds);

        private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

        private static ChatActor BuildActor(ChannelChatContext channel, ChatSender sender)
        {
            channel.Roles.TryGetValue(sender.UserId, out var role);
            return new ChatActor(sender, channel.OwnerUserId == sender.UserId, role);
        }

        // ── Tipos internos ────────────────────────────────────────────────

        private sealed record ChannelChatContext(
            int ChannelId,
            string OwnerUserId,
            IReadOnlyDictionary<string, string> Emotes,
            ChatFilter Filter,
            IReadOnlyDictionary<string, ChannelRoleType> Roles);

        private sealed record ChatActor(ChatSender Sender, bool IsBroadcaster, ChannelRoleType Role)
        {
            public bool IsAdmin => Sender.IsAdmin;
            public bool IsModerator => IsBroadcaster || IsAdmin || Role == ChannelRoleType.Moderator;
            public bool IsVip => Role == ChannelRoleType.Vip;
            public bool CanManageRoles => IsBroadcaster || IsAdmin;

            public List<string> Badges()
            {
                var badges = new List<string>();
                if (IsBroadcaster) badges.Add("broadcaster");
                else if (Role == ChannelRoleType.Moderator) badges.Add("moderator");
                else if (Role == ChannelRoleType.Vip) badges.Add("vip");
                if (IsAdmin) badges.Add("admin");
                return badges;
            }
        }

        private sealed record BanSnapshot(DateTime? ExpiresAtUtc);

        private sealed class UserThrottleState
        {
            public readonly Queue<DateTime> Recent = new();
            public DateTime LastAcceptedUtc = DateTime.MinValue;
            public string? LastText;
        }
    }
}
