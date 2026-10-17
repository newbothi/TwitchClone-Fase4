using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    public interface IChatFilterService
    {
        Task<ChatFilter> GetFiltersAsync(string channelId);
        Task AddBannedWordAsync(string channelId, string word);
        Task RemoveBannedWordAsync(string channelId, string word);
        Task SetSlowModeAsync(string channelId, bool enabled, int seconds = 30);
        Task SetFollowersOnlyAsync(string channelId, bool enabled);
        Task SetSubsOnlyAsync(string channelId, bool enabled);
        Task<bool> MessagePassesFiltersAsync(string channelId, string content);

        // ── Fase 2 ─────────────────────────────────────────────
        Task SetEmoteOnlyAsync(string channelId, bool enabled);
        Task SetBlockLinksAsync(string channelId, bool enabled);
        Task SetAutoModAsync(string channelId, bool enabled);
        Task SetRulesAsync(string channelId, string rules);
        Task SetPinnedMessageAsync(string channelId, ChatMessageDto? message);

        // ── Fase 4 ─────────────────────────────────────────────
        Task SetBlockCapsAsync(string channelId, bool enabled);
        Task SetBlockRepeatedCharsAsync(string channelId, bool enabled);
    }
}
