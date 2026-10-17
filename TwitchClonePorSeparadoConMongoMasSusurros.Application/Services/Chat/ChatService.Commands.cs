using Microsoft.Extensions.Logging;
using System.Text;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat
{
    // Comandos de chat, como en Twitch. Los nombres de usuario son el nombre público
    // (DisplayUsername) y se aceptan con o sin "@".
    //
    //  Todos:        /me, /color, /w, /mods, /vips, /help
    //  Moderadores:  /ban, /unban, /timeout, /untimeout, /clear, /slow, /slowoff,
    //                /followers, /followersoff, /emoteonly, /emoteonlyoff,
    //                /announce, /poll, /endpoll, /unpin
    //  Dueño/Admin:  /mod, /unmod, /vip, /unvip, /raid
    public partial class ChatService
    {
        private const int DefaultTimeoutSeconds = 600;

        private async Task<ChatActionResult> ExecuteCommandAsync(ChannelChatContext ctx, ChatActor actor, ChatUserProfile profile, string text, CancellationToken ct)
        {
            var parts = text[1..].Split(' ', 2, StringSplitOptions.TrimEntries);
            var command = parts[0].ToLowerInvariant();
            var rest = parts.Length > 1 ? parts[1] : string.Empty;
            var args = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var channelId = ctx.ChannelId;

            // ── Comandos para todos ───────────────────────────────────────────
            switch (command)
            {
                case "help":
                case "ayuda":
                    return ChatActionResult.Success(HelpText(actor));

                case "color":
                    return await ColorCommandAsync(actor, args);

                case "w":
                case "whisper":
                    return await WhisperCommandAsync(actor, profile, rest);

                case "mods":
                case "vips":
                    return await ListRolesCommandAsync(channelId, command == "mods" ? ChannelRoleType.Moderator : ChannelRoleType.Vip);

                case "me":
                    return ChatActionResult.Fail("Escribe algo después de /me.");
            }

            // ── Comandos de moderador ─────────────────────────────────────────
            var modCommands = new HashSet<string>
            {
                "ban", "unban", "timeout", "untimeout", "clear", "slow", "slowoff", "followers", "followersoff",
                "emoteonly", "emoteonlyoff", "announce", "poll", "endpoll", "unpin"
            };
            var ownerCommands = new HashSet<string> { "mod", "unmod", "vip", "unvip", "raid" };

            if (modCommands.Contains(command) && !actor.IsModerator)
                return ChatActionResult.Fail($"Solo los moderadores pueden usar /{command}.");
            if (ownerCommands.Contains(command) && !actor.CanManageRoles)
                return ChatActionResult.Fail($"Solo el dueño del canal puede usar /{command}.");

            switch (command)
            {
                case "ban":
                {
                    var target = await ResolveUserArgAsync(args);
                    if (target.Error != null) return target.Error;
                    var reason = args.Length > 1 ? string.Join(' ', args.Skip(1)) : null;
                    return await BanAsync(channelId, target.User!.Id, reason, actor.Sender);
                }

                case "timeout":
                {
                    var target = await ResolveUserArgAsync(args);
                    if (target.Error != null) return target.Error;

                    var seconds = DefaultTimeoutSeconds;
                    var reasonStart = 1;
                    if (args.Length > 1)
                    {
                        var parsed = ChatText.ParseDuration(args[1]);
                        if (parsed == null) return ChatActionResult.Fail("Duración inválida. Ejemplos: /timeout usuario 600, 10m, 1h, 1d");
                        seconds = parsed.Value;
                        reasonStart = 2;
                    }
                    var reason = args.Length > reasonStart ? string.Join(' ', args.Skip(reasonStart)) : null;
                    return await TimeoutAsync(channelId, target.User!.Id, seconds, reason, actor.Sender);
                }

                case "unban":
                case "untimeout":
                {
                    var target = await ResolveUserArgAsync(args);
                    if (target.Error != null) return target.Error;
                    return await UnbanAsync(channelId, target.User!.Id, actor.Sender);
                }

                case "clear":
                    return await ClearAsync(channelId, actor.Sender);

                case "slow":
                {
                    var seconds = 30;
                    if (args.Length > 0)
                    {
                        var parsed = ChatText.ParseDuration(args[0]);
                        if (parsed == null || parsed > MaxSlowModeSeconds) return ChatActionResult.Fail($"Usa /slow [segundos] (máximo {MaxSlowModeSeconds}).");
                        seconds = parsed.Value;
                    }
                    await _filters.SetSlowModeAsync(channelId.ToString(), true, seconds);
                    return await SettingsChangedAsync(channelId, $"Modo lento activado: {seconds} s entre mensajes.");
                }

                case "slowoff":
                    await _filters.SetSlowModeAsync(channelId.ToString(), false, 0);
                    return await SettingsChangedAsync(channelId, "Modo lento desactivado.");

                case "followers":
                    await _filters.SetFollowersOnlyAsync(channelId.ToString(), true);
                    return await SettingsChangedAsync(channelId, "Modo solo seguidores activado.");

                case "followersoff":
                    await _filters.SetFollowersOnlyAsync(channelId.ToString(), false);
                    return await SettingsChangedAsync(channelId, "Modo solo seguidores desactivado.");

                case "emoteonly":
                    await _filters.SetEmoteOnlyAsync(channelId.ToString(), true);
                    return await SettingsChangedAsync(channelId, "Modo solo emotes activado.");

                case "emoteonlyoff":
                    await _filters.SetEmoteOnlyAsync(channelId.ToString(), false);
                    return await SettingsChangedAsync(channelId, "Modo solo emotes desactivado.");

                case "announce":
                {
                    var announcement = Normalize(rest);
                    if (announcement.Length == 0) return ChatActionResult.Fail("Usa /announce texto del anuncio.");
                    if (announcement.Length > MaxMessageLength) return ChatActionResult.Fail($"El anuncio supera los {MaxMessageLength} caracteres.");

                    var message = await BuildMessageAsync(ctx, actor, profile, announcement,
                        EmoteParser.Parse(announcement, ctx.Emotes), UtcNow(), isAction: false, isAnnouncement: true, replyToId: null);
                    var dto = await PublishAsync(message, channelId);
                    return ChatActionResult.Success(null, new BroadcastMessageEffect(dto));
                }

                case "poll":
                    return PollCommand(channelId, profile, rest);

                case "endpoll":
                {
                    var ended = _polls.End(channelId);
                    return ended == null
                        ? ChatActionResult.Fail("No hay una encuesta activa.")
                        : ChatActionResult.Success("Encuesta terminada.", new PollEffect(ended));
                }

                case "unpin":
                    return await UnpinAsync(channelId, actor.Sender);

                case "mod":
                case "unmod":
                case "vip":
                case "unvip":
                {
                    var target = await ResolveUserArgAsync(args);
                    if (target.Error != null) return target.Error;
                    var role = command switch
                    {
                        "mod" => ChannelRoleType.Moderator,
                        "vip" => ChannelRoleType.Vip,
                        _ => ChannelRoleType.None
                    };

                    // /unmod a alguien que es VIP (o al revés) no debe quitarle el otro rol.
                    if (role == ChannelRoleType.None)
                    {
                        ctx.Roles.TryGetValue(target.User!.Id, out var current);
                        var expected = command == "unmod" ? ChannelRoleType.Moderator : ChannelRoleType.Vip;
                        if (current != expected)
                            return ChatActionResult.Fail($"{target.User.DisplayUsername} no es {(expected == ChannelRoleType.Moderator ? "moderador" : "VIP")}.");
                    }
                    return await SetRoleAsync(channelId, target.User!.Id, role, actor.Sender);
                }

                case "raid":
                    return await RaidCommandAsync(ctx, args);
            }

            return ChatActionResult.Fail($"Comando desconocido: /{command}. Escribe /help para ver los comandos.");
        }

        // ── Implementaciones ──────────────────────────────────────────────────

        private async Task<ChatActionResult> SettingsChangedAsync(int channelId, string notice)
        {
            InvalidateChannel(channelId);
            return ChatActionResult.Success(notice, new SettingsChangedEffect(await GetSettingsAsync(channelId)));
        }

        private async Task<(ChatUserLookup? User, ChatActionResult? Error)> ResolveUserArgAsync(string[] args)
        {
            if (args.Length == 0) return (null, ChatActionResult.Fail("Falta el nombre del usuario."));
            var name = args[0].TrimStart('@');
            var user = await _chatUsers.FindByDisplayNameAsync(name);
            return user == null
                ? (null, ChatActionResult.Fail($"No encontré al usuario «{name}»."))
                : (user, null);
        }

        private async Task<ChatActionResult> ColorCommandAsync(ChatActor actor, string[] args)
        {
            if (args.Length == 0)
                return ChatActionResult.Fail($"Usa /color #RRGGBB o un nombre: {string.Join(", ", ChatText.ColorNames.Distinct())}. /color reset vuelve al color automático.");

            string? color = null;
            if (!args[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                var (parsed, error) = ChatText.ParseColor(args[0]);
                if (error != null) return ChatActionResult.Fail(error);
                color = parsed;
            }

            await _userSettings.SetColorAsync(actor.Sender.UserId, color);
            _cache.Remove($"chat:color:{actor.Sender.UserId}");
            return ChatActionResult.Success(color == null ? "Volviste a tu color automático." : $"Tu nombre ahora se ve en {color}.");
        }

        private async Task<ChatActionResult> WhisperCommandAsync(ChatActor actor, ChatUserProfile profile, string rest)
        {
            var parts = rest.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[1].Length == 0) return ChatActionResult.Fail("Usa /w usuario mensaje.");

            var target = await _chatUsers.FindByDisplayNameAsync(parts[0].TrimStart('@'));
            if (target == null) return ChatActionResult.Fail($"No encontré al usuario «{parts[0].TrimStart('@')}».");
            if (target.Id == actor.Sender.UserId) return ChatActionResult.Fail("No puedes susurrarte a ti mismo.");

            var content = Normalize(parts[1]);
            if (content.Length > MaxMessageLength) content = content[..MaxMessageLength];

            try
            {
                var whisper = await _whispers.SendAsync(actor.Sender.UserId, profile.DisplayName, target.Id, target.DisplayUsername, content);
                return ChatActionResult.Success($"Susurro enviado a {target.DisplayUsername}.",
                    new WhisperEffect(target.Id, whisper.ConversationId, profile.DisplayName, content));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo enviar el susurro.");
                return ChatActionResult.Fail("No se pudo enviar el susurro. Intenta de nuevo.");
            }
        }

        private async Task<ChatActionResult> ListRolesCommandAsync(int channelId, ChannelRoleType role)
        {
            var entries = (await _roles.ListAsync(channelId)).Where(e => e.Role == role).Select(e => e.DisplayName).OrderBy(n => n).ToList();
            var label = role == ChannelRoleType.Moderator ? "Moderadores" : "VIP";
            return ChatActionResult.Success(entries.Count == 0
                ? $"Este canal no tiene {label.ToLowerInvariant()}."
                : $"{label} ({entries.Count}): {string.Join(", ", entries)}");
        }

        // /poll [duración] Pregunta | opción 1 | opción 2 [| ...]
        private ChatActionResult PollCommand(int channelId, ChatUserProfile profile, string rest)
        {
            var segments = rest.Split('|', StringSplitOptions.TrimEntries);
            if (segments.Length < 3)
                return ChatActionResult.Fail("Usa /poll [duración] ¿Pregunta? | opción 1 | opción 2 (hasta 5 opciones). Ej: /poll 2m ¿Qué juego sigue? | Elden Ring | Minecraft");

            var question = segments[0];
            var duration = TimeSpan.FromSeconds(60);
            var firstSpace = question.IndexOf(' ');
            if (firstSpace > 0 && ChatText.ParseDuration(question[..firstSpace]) is { } seconds)
            {
                duration = TimeSpan.FromSeconds(seconds);
                question = question[(firstSpace + 1)..].Trim();
            }

            var (poll, error) = _polls.Start(channelId, question, segments.Skip(1).ToList(), duration, profile.DisplayName);
            return poll == null
                ? ChatActionResult.Fail(error ?? "No se pudo crear la encuesta.")
                : ChatActionResult.Success(null, new PollEffect(poll));
        }

        private async Task<ChatActionResult> RaidCommandAsync(ChannelChatContext ctx, string[] args)
        {
            var target = await ResolveUserArgAsync(args);
            if (target.Error != null) return target.Error;

            var targetChannel = await _channels.GetByUserIdAsync(target.User!.Id);
            if (targetChannel == null) return ChatActionResult.Fail($"{target.User.DisplayUsername} no tiene canal.");
            if (targetChannel.Id == ctx.ChannelId) return ChatActionResult.Fail("No puedes raidear tu propio canal.");

            var info = await _channels.GetStreamInfoAsync(targetChannel.Id);
            if (info == null || !info.IsLive) return ChatActionResult.Fail($"{target.User.DisplayUsername} no está en vivo.");

            var sourceName = (await GetUserProfileAsync(ctx.OwnerUserId))?.DisplayName ?? "Un canal";
            var viewers = _presence.GetCount(ctx.ChannelId);

            return ChatActionResult.Success(
                $"🚀 Raid hacia {target.User.DisplayUsername} con {viewers} espectador{(viewers == 1 ? "" : "es")}.",
                new RaidEffect(ctx.ChannelId, sourceName, targetChannel.Id, target.User.DisplayUsername, viewers));
        }

        private static string HelpText(ChatActor actor)
        {
            var sb = new StringBuilder("Comandos: /me acción · /color · /w usuario mensaje · /mods · /vips");
            if (actor.IsModerator)
                sb.Append(" | Moderación: /ban usuario [motivo] · /unban · /timeout usuario [10m|1h] · /untimeout · /clear · /slow [s] · /slowoff · /followers · /followersoff · /emoteonly · /emoteonlyoff · /announce · /poll · /endpoll · /unpin");
            if (actor.CanManageRoles)
                sb.Append(" | Dueño: /mod · /unmod · /vip · /unvip · /raid canal");
            return sb.ToString();
        }
    }
}
