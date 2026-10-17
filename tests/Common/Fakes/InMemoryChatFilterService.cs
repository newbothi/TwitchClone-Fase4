using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes
{
    // Reemplaza a ChatFilterService (colección ChatFilters de MongoDB).
    public sealed class InMemoryChatFilterService : IChatFilterService
    {
        private readonly InMemoryPlatform _db;

        public InMemoryChatFilterService(InMemoryPlatform db) => _db = db;

        // Devuelve una COPIA, igual que MongoDB (cada lectura deserializa un objeto nuevo).
        // Si devolviera el objeto guardado, la caché del ChatService "vería" los cambios
        // aunque nadie la invalidara, y las pruebas no detectarían ese error.
        public Task<ChatFilter> GetFiltersAsync(string channelId)
        {
            if (_db.FailChatFilterReads) throw new InvalidOperationException("MongoDB no disponible (simulado).");
            var stored = Get(channelId);
            lock (stored)
            {
                return Task.FromResult(new ChatFilter
                {
                    Id = stored.Id,
                    ChannelId = stored.ChannelId,
                    BannedWords = stored.BannedWords.ToList(),
                    SlowModeEnabled = stored.SlowModeEnabled,
                    SlowModeSeconds = stored.SlowModeSeconds,
                    FollowersOnlyMode = stored.FollowersOnlyMode,
                    SubsOnlyMode = stored.SubsOnlyMode,
                    UpdatedAt = stored.UpdatedAt,
                    EmoteOnlyMode = stored.EmoteOnlyMode,
                    BlockLinks = stored.BlockLinks,
                    AutoModEnabled = stored.AutoModEnabled,
                    Rules = stored.Rules,
                    PinnedMessage = stored.PinnedMessage,
                    BlockExcessiveCaps = stored.BlockExcessiveCaps,
                    BlockRepeatedChars = stored.BlockRepeatedChars
                });
            }
        }

        public Task AddBannedWordAsync(string channelId, string word)
        {
            var filter = Get(channelId);
            lock (filter)
            {
                var normalized = word.Trim().ToLowerInvariant();
                if (!filter.BannedWords.Contains(normalized)) filter.BannedWords.Add(normalized);
            }
            return Task.CompletedTask;
        }

        public Task RemoveBannedWordAsync(string channelId, string word)
        {
            var filter = Get(channelId);
            lock (filter) filter.BannedWords.Remove(word.Trim().ToLowerInvariant());
            return Task.CompletedTask;
        }

        public Task SetSlowModeAsync(string channelId, bool enabled, int seconds = 30)
        {
            var filter = Get(channelId);
            filter.SlowModeEnabled = enabled;
            filter.SlowModeSeconds = enabled ? seconds : 0;
            return Task.CompletedTask;
        }

        public Task SetFollowersOnlyAsync(string channelId, bool enabled)
        {
            Get(channelId).FollowersOnlyMode = enabled;
            return Task.CompletedTask;
        }

        public Task SetSubsOnlyAsync(string channelId, bool enabled)
        {
            Get(channelId).SubsOnlyMode = enabled;
            return Task.CompletedTask;
        }

        public Task SetEmoteOnlyAsync(string channelId, bool enabled) => Update(channelId, f => f.EmoteOnlyMode = enabled);
        public Task SetBlockLinksAsync(string channelId, bool enabled) => Update(channelId, f => f.BlockLinks = enabled);
        public Task SetAutoModAsync(string channelId, bool enabled) => Update(channelId, f => f.AutoModEnabled = enabled);
        public Task SetRulesAsync(string channelId, string rules) => Update(channelId, f => f.Rules = rules);
        public Task SetPinnedMessageAsync(string channelId, ChatMessageDto? message) => Update(channelId, f => f.PinnedMessage = message);
        public Task SetBlockCapsAsync(string channelId, bool enabled) => Update(channelId, f => f.BlockExcessiveCaps = enabled);
        public Task SetBlockRepeatedCharsAsync(string channelId, bool enabled) => Update(channelId, f => f.BlockRepeatedChars = enabled);

        private Task Update(string channelId, Action<ChatFilter> change)
        {
            var filter = Get(channelId);
            lock (filter) change(filter);
            return Task.CompletedTask;
        }

        public Task<bool> MessagePassesFiltersAsync(string channelId, string content)
        {
            var filter = Get(channelId);
            var lower = content.ToLowerInvariant();
            return Task.FromResult(!filter.BannedWords.Any(w => lower.Contains(w)));
        }

        private ChatFilter Get(string channelId) =>
            _db.Filters.GetOrAdd(channelId, key => new ChatFilter { ChannelId = key });
    }
}
