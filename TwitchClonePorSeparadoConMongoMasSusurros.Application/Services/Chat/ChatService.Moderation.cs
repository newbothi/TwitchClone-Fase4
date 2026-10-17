using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat
{
    // Moderación, roles, mensaje fijado, AutoMod, encuestas y tarjeta de usuario.
    //
    // Reglas de permisos (como en Twitch):
    //  • Moderan: el dueño del canal, sus moderadores y los Admin de la plataforma.
    //  • Nadie puede sancionar al dueño del canal.
    //  • Un moderador no puede sancionar a otro moderador (el dueño y los Admin sí).
    //  • Solo el dueño y los Admin nombran o quitan moderadores y VIP.
    public partial class ChatService
    {
        public async Task<bool> CanModerateAsync(int channelId, ChatSender user)
        {
            var ctx = await GetChannelContextAsync(channelId);
            return ctx != null && BuildActor(ctx, user).IsModerator;
        }

        public async Task<ChatActionResult> DeleteMessageAsync(int channelId, string messageId, ChatSender moderator)
        {
            var (ctx, actor, error) = await RequireModeratorAsync(channelId, moderator);
            if (error != null) return error;

            var message = await FindMessageAsync(channelId, messageId);
            if (message == null) return ChatActionResult.Fail("Mensaje no encontrado.");

            if (message.UserId != actor!.Sender.UserId && CanSanctionError(ctx!, actor, message.UserId) is { } denied)
                return ChatActionResult.Fail(denied);

            await _messages.FlushAsync();
            await _messages.DeleteMessageAsync(messageId);
            _recent.Remove(channelId, messageId);

            var result = new ChatActionResult { Ok = true, TargetDisplayName = message.DisplayName, Effects = { new MessageDeletedEffect(messageId) } };

            // Fase 4: un mod borró el mensaje de otra persona → ejemplo (débil) de mensaje tóxico para AutoMod.
            // Borrar uno propio no dice nada sobre toxicidad.
            if (message.UserId != actor.Sender.UserId && !actor.Sender.IsSimulated)
                result.Effects.Add(new AutoModFeedbackEffect(message.PlainText(), IsToxic: true, Category: null, AutoModFeedbackEffect.Deleted));

            return result;
        }

        public async Task<ChatActionResult> TimeoutAsync(int channelId, string targetUserId, int seconds, string? reason, ChatSender moderator)
        {
            var (ctx, actor, target, error) = await ValidateSanctionAsync(channelId, targetUserId, moderator);
            if (error != null) return error;

            seconds = Math.Clamp(seconds, 1, MaxTimeoutSeconds);
            var ok = await _moderation.TimeoutAsync(channelId, targetUserId, actor!.Sender.UserId, TimeSpan.FromSeconds(seconds),
                string.IsNullOrWhiteSpace(reason) ? $"Timeout de {ChatText.FormatDuration(seconds)}" : Truncate(reason, 500));
            if (!ok) return ChatActionResult.Fail("No se pudo silenciar a este usuario.");

            await PurgeUserMessagesAsync(channelId, targetUserId);
            InvalidateBan(channelId, targetUserId);

            return new ChatActionResult
            {
                Ok = true,
                TargetDisplayName = target!.DisplayName,
                Notice = $"{target.DisplayName} fue silenciado por {ChatText.FormatDuration(seconds)}.",
                Effects = { new UserSanctionedEffect(targetUserId, target.DisplayName, false, seconds) }
            };
        }

        public async Task<ChatActionResult> BanAsync(int channelId, string targetUserId, string? reason, ChatSender moderator)
        {
            var (ctx, actor, target, error) = await ValidateSanctionAsync(channelId, targetUserId, moderator);
            if (error != null) return error;

            var ok = await _moderation.BanAsync(channelId, targetUserId, actor!.Sender.UserId,
                string.IsNullOrWhiteSpace(reason) ? "Sin motivo" : Truncate(reason, 500));
            if (!ok) return ChatActionResult.Fail("No se pudo banear a este usuario.");

            await PurgeUserMessagesAsync(channelId, targetUserId);
            InvalidateBan(channelId, targetUserId);

            // Un baneado deja de ser mod/VIP del canal.
            if (ctx!.Roles.ContainsKey(targetUserId))
            {
                await _roles.SetRoleAsync(channelId, targetUserId, ChannelRoleType.None, actor.Sender.UserId);
                InvalidateChannel(channelId);
            }

            return new ChatActionResult
            {
                Ok = true,
                TargetDisplayName = target!.DisplayName,
                Notice = $"{target.DisplayName} fue baneado del chat.",
                Effects = { new UserSanctionedEffect(targetUserId, target.DisplayName, true, 0) }
            };
        }

        public async Task<ChatActionResult> UnbanAsync(int channelId, string targetUserId, ChatSender moderator)
        {
            var (_, _, error) = await RequireModeratorAsync(channelId, moderator);
            if (error != null) return error;

            var target = await GetUserProfileAsync(targetUserId);
            var removed = await _moderation.UnbanAsync(channelId, targetUserId);
            InvalidateBan(channelId, targetUserId);

            if (!removed) return ChatActionResult.Fail("Ese usuario no estaba baneado ni silenciado.");

            var name = target?.DisplayName ?? "El usuario";
            return new ChatActionResult
            {
                Ok = true,
                TargetDisplayName = target?.DisplayName,
                Notice = $"{name} ya puede volver a escribir.",
                Effects = { new UserUnbannedEffect(targetUserId, name) }
            };
        }

        public async Task<ChatActionResult> ClearAsync(int channelId, ChatSender moderator)
        {
            var (_, _, error) = await RequireModeratorAsync(channelId, moderator);
            if (error != null) return error;

            await _messages.FlushAsync();
            await _messages.ClearChannelAsync(channelId.ToString());
            _recent.Clear(channelId);
            return ChatActionResult.Success("Chat limpiado.", new ChatClearedEffect());
        }

        public async Task<ChatActionResult> SetRoleAsync(int channelId, string targetUserId, ChannelRoleType role, ChatSender actorSender)
        {
            var ctx = await GetChannelContextAsync(channelId);
            if (ctx == null) return ChatActionResult.Fail("Este canal no existe.");

            var actor = BuildActor(ctx, actorSender);
            if (!actor.CanManageRoles)
                return ChatActionResult.Fail("Solo el dueño del canal puede nombrar moderadores y VIP.");
            if (targetUserId == ctx.OwnerUserId)
                return ChatActionResult.Fail("El dueño del canal ya tiene todos los permisos.");

            var target = await GetUserProfileAsync(targetUserId);
            if (target == null) return ChatActionResult.Fail("Usuario no encontrado.");

            ctx.Roles.TryGetValue(targetUserId, out var current);
            if (current == role)
            {
                return ChatActionResult.Fail(role switch
                {
                    ChannelRoleType.Moderator => $"{target.DisplayName} ya es moderador.",
                    ChannelRoleType.Vip => $"{target.DisplayName} ya es VIP.",
                    _ => $"{target.DisplayName} no tiene un rol en este canal."
                });
            }

            await _roles.SetRoleAsync(channelId, targetUserId, role, actor.Sender.UserId);
            InvalidateChannel(channelId);

            var notice = role switch
            {
                ChannelRoleType.Moderator => $"{target.DisplayName} ahora es moderador.",
                ChannelRoleType.Vip => $"{target.DisplayName} ahora es VIP.",
                _ => $"{target.DisplayName} ya no es {(current == ChannelRoleType.Moderator ? "moderador" : "VIP")}."
            };

            return new ChatActionResult
            {
                Ok = true,
                TargetDisplayName = target.DisplayName,
                Notice = notice,
                Effects = { new RoleChangedEffect(targetUserId, target.DisplayName, role) }
            };
        }

        // ── Mensaje fijado ─────────────────────────────────────────────────

        public async Task<ChatMessageDto?> GetPinnedAsync(int channelId)
            => (await GetChannelContextAsync(channelId))?.Filter.PinnedMessage;

        public async Task<ChatActionResult> PinAsync(int channelId, string messageId, ChatSender moderator)
        {
            var (_, _, error) = await RequireModeratorAsync(channelId, moderator);
            if (error != null) return error;

            var message = await FindMessageAsync(channelId, messageId);
            if (message == null) return ChatActionResult.Fail("Mensaje no encontrado.");

            await _filters.SetPinnedMessageAsync(channelId.ToString(), message);
            InvalidateChannel(channelId);
            return ChatActionResult.Success("Mensaje fijado.", new PinChangedEffect(message));
        }

        public async Task<ChatActionResult> UnpinAsync(int channelId, ChatSender moderator)
        {
            var (_, _, error) = await RequireModeratorAsync(channelId, moderator);
            if (error != null) return error;

            await _filters.SetPinnedMessageAsync(channelId.ToString(), null);
            InvalidateChannel(channelId);
            return ChatActionResult.Success("Mensaje desfijado.", new PinChangedEffect(null));
        }

        // ── AutoMod ────────────────────────────────────────────────────────

        public IReadOnlyList<AutoModHeldDto> GetHeldMessages(int channelId) => _autoModQueue.List(channelId, UtcNow());

        public async Task<ChatActionResult> ResolveAutoModAsync(int channelId, string heldId, bool approve, ChatSender moderator)
        {
            var (_, actor, error) = await RequireModeratorAsync(channelId, moderator);
            if (error != null) return error;

            var item = _autoModQueue.Take(channelId, heldId, UtcNow());
            if (item == null) return ChatActionResult.Fail("Ese mensaje ya fue revisado o expiró.");

            var (held, pending) = item.Value;
            var modName = (await GetUserProfileAsync(actor!.Sender.UserId))?.DisplayName ?? "un moderador";
            var effects = new List<ChatEffect>();

            if (approve)
            {
                var dto = await PublishAsync(pending, channelId);
                effects.Add(new BroadcastMessageEffect(dto));
                effects.Add(new UserNoticeEffect(held.UserId, "Un moderador aprobó tu mensaje."));
            }
            else
            {
                effects.Add(new UserNoticeEffect(held.UserId, "Un moderador rechazó tu mensaje."));
            }
            effects.Add(new AutoModResolvedEffect(held.Id, held.UserId, approve, modName));

            // Fase 4: la decisión del moderador entrena al modelo (aprobado = falso positivo).
            if (!actor.Sender.IsSimulated)
                effects.Add(new AutoModFeedbackEffect(held.Text, IsToxic: !approve, held.Category,
                    approve ? AutoModFeedbackEffect.Approved : AutoModFeedbackEffect.Rejected));

            return new ChatActionResult { Ok = true, Effects = effects };
        }

        // ── Encuestas ──────────────────────────────────────────────────────

        public PollDto? GetActivePoll(int channelId) => _polls.GetActive(channelId);

        public Task<ChatActionResult> VotePollAsync(int channelId, string pollId, int optionIndex, ChatSender voter)
        {
            var (poll, error) = _polls.Vote(channelId, pollId, voter.UserId, optionIndex);
            return Task.FromResult(poll == null
                ? ChatActionResult.Fail(error ?? "No se pudo votar.")
                : ChatActionResult.Success()); // el PollTicker difunde el resultado 1 vez por segundo
        }

        // ── Tarjeta de usuario ────────────────────────────────────────────

        public async Task<UserCardDto?> GetUserCardAsync(int channelId, string targetUserId, ChatSender? viewer)
        {
            var ctx = await GetChannelContextAsync(channelId);
            if (ctx == null) return null;

            var target = await GetUserProfileAsync(targetUserId);
            if (target == null) return null;

            ctx.Roles.TryGetValue(targetUserId, out var role);
            var isOwner = targetUserId == ctx.OwnerUserId;

            var card = new UserCardDto
            {
                UserId = targetUserId,
                DisplayName = target.DisplayName,
                Color = await GetColorAsync(targetUserId),
                Role = isOwner ? "broadcaster" : role switch
                {
                    ChannelRoleType.Moderator => "moderator",
                    ChannelRoleType.Vip => "vip",
                    _ => "viewer"
                },
                AccountCreatedAt = DateTime.SpecifyKind(target.CreatedAt, DateTimeKind.Local).ToUniversalTime(),
                FollowingSince = isOwner ? null : await SafeFollowDateAsync(targetUserId, ctx.OwnerUserId)
            };
            if (card.Role != "viewer") card.Badges.Add(card.Role);

            if (viewer != null)
            {
                var actor = BuildActor(ctx, viewer);
                if (actor.IsModerator && targetUserId != viewer.UserId)
                {
                    var canSanction = CanSanctionError(ctx, actor, targetUserId) == null;
                    card.CanTimeout = canSanction;
                    card.CanBan = canSanction;
                    card.CanManageRoles = actor.CanManageRoles && !isOwner;

                    var ban = await _moderation.GetActiveBanAsync(channelId, targetUserId);
                    if (ban != null)
                    {
                        card.IsBanned = ban.ExpiresAt == null;
                        card.TimedOutUntil = ban.ExpiresAt.HasValue ? DateTime.SpecifyKind(ban.ExpiresAt.Value, DateTimeKind.Utc) : null;
                    }
                }
            }

            return card;
        }

        // ── Utilidades ─────────────────────────────────────────────────────

        private async Task<(ChannelChatContext? Ctx, ChatActor? Actor, ChatActionResult? Error)> RequireModeratorAsync(int channelId, ChatSender sender)
        {
            var ctx = await GetChannelContextAsync(channelId);
            if (ctx == null) return (null, null, ChatActionResult.Fail("Este canal no existe."));

            var actor = BuildActor(ctx, sender);
            return actor.IsModerator
                ? (ctx, actor, null)
                : (ctx, actor, ChatActionResult.Fail("No tienes permisos de moderación en este canal."));
        }

        private async Task<(ChannelChatContext? Ctx, ChatActor? Actor, ChatUserProfile? Target, ChatActionResult? Error)> ValidateSanctionAsync(
            int channelId, string targetUserId, ChatSender moderator)
        {
            var (ctx, actor, error) = await RequireModeratorAsync(channelId, moderator);
            if (error != null) return (ctx, actor, null, error);

            if (string.IsNullOrWhiteSpace(targetUserId))
                return (ctx, actor, null, ChatActionResult.Fail("Usuario no encontrado."));
            if (targetUserId == moderator.UserId)
                return (ctx, actor, null, ChatActionResult.Fail("No puedes aplicarte una sanción a ti mismo."));
            if (CanSanctionError(ctx!, actor!, targetUserId) is { } denied)
                return (ctx, actor, null, ChatActionResult.Fail(denied));

            var target = await GetUserProfileAsync(targetUserId);
            return target == null
                ? (ctx, actor, null, ChatActionResult.Fail("Usuario no encontrado."))
                : (ctx, actor, target, null);
        }

        // null = puede; si no, el motivo.
        private static string? CanSanctionError(ChannelChatContext ctx, ChatActor actor, string targetUserId)
        {
            if (targetUserId == ctx.OwnerUserId) return "No puedes sancionar al dueño del canal.";
            if (ctx.Roles.TryGetValue(targetUserId, out var role) && role == ChannelRoleType.Moderator
                && !actor.IsBroadcaster && !actor.IsAdmin)
                return "Un moderador no puede sancionar a otro moderador.";
            return null;
        }

        private async Task<ChatMessageDto?> FindMessageAsync(int channelId, string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId)) return null;

            var cached = _recent.Find(channelId, messageId);
            if (cached != null) return cached;
            if (!ObjectId.TryParse(messageId, out _)) return null;

            await _messages.FlushAsync();
            var stored = await _messages.GetByIdAsync(messageId);
            return stored != null && stored.ChannelId == channelId.ToString() && !stored.IsDeleted
                ? ToDto(stored, channelId)
                : null;
        }

        private async Task PurgeUserMessagesAsync(int channelId, string userId)
        {
            _recent.RemoveUser(channelId, userId);
            try
            {
                await _messages.FlushAsync();
                await _messages.DeleteUserMessagesAsync(channelId.ToString(), userId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudieron borrar los mensajes del usuario {UserId} en el canal {ChannelId}.", userId, channelId);
            }
        }

        private async Task<DateTime?> SafeFollowDateAsync(string followerId, string followedId)
        {
            try
            {
                var date = await _chatUsers.GetFollowDateAsync(followerId, followedId);
                return date.HasValue ? DateTime.SpecifyKind(date.Value, DateTimeKind.Local).ToUniversalTime() : null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No se pudo leer la fecha de follow.");
                return null;
            }
        }

        private static string Truncate(string text, int max)
        {
            text = text.Trim();
            return text.Length > max ? text[..max] : text;
        }
    }
}
