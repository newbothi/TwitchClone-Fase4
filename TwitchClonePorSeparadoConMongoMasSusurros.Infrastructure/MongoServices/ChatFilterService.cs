using Microsoft.Extensions.Options;
using MongoDB.Driver;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public class ChatFilterService : IChatFilterService
    {
        private readonly IMongoCollection<ChatFilter> _filters;

        public ChatFilterService(IMongoClient mongoClient, IOptions<MongoSettings> settings)
        {
            var database = mongoClient.GetDatabase(settings.Value.DatabaseName);
            _filters = database.GetCollection<ChatFilter>("ChatFilters");
        }

        public async Task<ChatFilter> GetFiltersAsync(string channelId)
        {
            var filter = await _filters
                .Find(f => f.ChannelId == channelId)
                .FirstOrDefaultAsync();

            if (filter == null)
            {
                filter = new ChatFilter { ChannelId = channelId };
                await _filters.InsertOneAsync(filter);
            }

            return filter;
        }

        public async Task AddBannedWordAsync(string channelId, string word)
        {
            var update = Builders<ChatFilter>.Update
                .AddToSet(f => f.BannedWords, word.ToLower())
                .Set(f => f.UpdatedAt, DateTime.UtcNow);

            await _filters.UpdateOneAsync(
                f => f.ChannelId == channelId,
                update,
                new UpdateOptions { IsUpsert = true });
        }

        public async Task RemoveBannedWordAsync(string channelId, string word)
        {
            var update = Builders<ChatFilter>.Update
                .Pull(f => f.BannedWords, word.ToLower())
                .Set(f => f.UpdatedAt, DateTime.UtcNow);

            await _filters.UpdateOneAsync(f => f.ChannelId == channelId, update);
        }

        public async Task SetSlowModeAsync(string channelId, bool enabled, int seconds = 30)
        {
            var update = Builders<ChatFilter>.Update
                .Set(f => f.SlowModeEnabled, enabled)
                .Set(f => f.SlowModeSeconds, enabled ? seconds : 0)
                .Set(f => f.UpdatedAt, DateTime.UtcNow);

            await _filters.UpdateOneAsync(
                f => f.ChannelId == channelId,
                update,
                new UpdateOptions { IsUpsert = true });
        }

        public async Task SetFollowersOnlyAsync(string channelId, bool enabled)
        {
            var update = Builders<ChatFilter>.Update
                .Set(f => f.FollowersOnlyMode, enabled)
                .Set(f => f.UpdatedAt, DateTime.UtcNow);

            await _filters.UpdateOneAsync(
                f => f.ChannelId == channelId,
                update,
                new UpdateOptions { IsUpsert = true });
        }

        public async Task SetSubsOnlyAsync(string channelId, bool enabled)
        {
            var update = Builders<ChatFilter>.Update
                .Set(f => f.SubsOnlyMode, enabled)
                .Set(f => f.UpdatedAt, DateTime.UtcNow);

            await _filters.UpdateOneAsync(
                f => f.ChannelId == channelId,
                update,
                new UpdateOptions { IsUpsert = true });
        }

        public async Task<bool> MessagePassesFiltersAsync(string channelId, string content)
        {
            var filter = await GetFiltersAsync(channelId);
            var lower = content.ToLower();
            return !filter.BannedWords.Any(word => lower.Contains(word));
        }

        // ── Fase 2 ─────────────────────────────────────────────────────────

        public Task SetEmoteOnlyAsync(string channelId, bool enabled)
            => SetFieldAsync(channelId, Builders<ChatFilter>.Update.Set(f => f.EmoteOnlyMode, enabled));

        public Task SetBlockLinksAsync(string channelId, bool enabled)
            => SetFieldAsync(channelId, Builders<ChatFilter>.Update.Set(f => f.BlockLinks, enabled));

        public Task SetAutoModAsync(string channelId, bool enabled)
            => SetFieldAsync(channelId, Builders<ChatFilter>.Update.Set(f => f.AutoModEnabled, enabled));

        public Task SetRulesAsync(string channelId, string rules)
            => SetFieldAsync(channelId, Builders<ChatFilter>.Update.Set(f => f.Rules, rules ?? string.Empty));

        public Task SetPinnedMessageAsync(string channelId, ChatMessageDto? message)
            => SetFieldAsync(channelId, Builders<ChatFilter>.Update.Set(f => f.PinnedMessage, message));

        // ── Fase 4 ─────────────────────────────────────────────────────────

        public Task SetBlockCapsAsync(string channelId, bool enabled)
            => SetFieldAsync(channelId, Builders<ChatFilter>.Update.Set(f => f.BlockExcessiveCaps, enabled));

        public Task SetBlockRepeatedCharsAsync(string channelId, bool enabled)
            => SetFieldAsync(channelId, Builders<ChatFilter>.Update.Set(f => f.BlockRepeatedChars, enabled));

        private async Task SetFieldAsync(string channelId, UpdateDefinition<ChatFilter> update)
        {
            await _filters.UpdateOneAsync(
                f => f.ChannelId == channelId,
                update.Set(f => f.UpdatedAt, DateTime.UtcNow),
                new UpdateOptions { IsUpsert = true });
        }
    }
}
