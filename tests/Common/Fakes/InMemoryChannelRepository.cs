using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes
{
    // Reemplaza a ChannelRepository (EF Core + Dapper sobre SQL Server).
    public sealed class InMemoryChannelRepository : IChannelRepository
    {
        private readonly InMemoryPlatform _db;

        public InMemoryChannelRepository(InMemoryPlatform db) => _db = db;

        public Task<string?> GetOwnerUserIdAsync(int channelId) =>
            Task.FromResult(_db.Channels.TryGetValue(channelId, out var c) ? c.UserId : null);

        public Task<IEnumerable<Emote>> GetEmotesAsync(int channelId)
        {
            if (!_db.Emotes.TryGetValue(channelId, out var list)) return Task.FromResult(Enumerable.Empty<Emote>());
            lock (list) return Task.FromResult<IEnumerable<Emote>>(list.ToList());
        }

        // Copia, igual que una consulta SQL real (un objeto nuevo por lectura).
        public Task<ChannelStreamInfo?> GetStreamInfoAsync(int channelId) =>
            Task.FromResult(_db.Channels.TryGetValue(channelId, out var c)
                ? new ChannelStreamInfo
                {
                    Id = c.Id,
                    UserId = c.UserId,
                    Name = c.Name,
                    StreamKey = c.StreamKey,
                    IsLive = c.IsLive,
                    OwnerDisplayName = c.OwnerDisplayName,
                    OwnerLockoutEnd = c.OwnerLockoutEnd,
                    CategoryName = c.CategoryName
                }
                : null);

        public Task<bool> SetLiveStateAsync(int channelId, bool isLive)
        {
            // Igual que el UPDATE ... WHERE IsLive <> @IsLive real: true solo si cambió.
            if (!_db.Channels.TryGetValue(channelId, out var c) || c.IsLive == isLive) return Task.FromResult(false);
            c.IsLive = isLive;
            _db.PersistedViewerCounts[channelId] = 0;
            return Task.FromResult(true);
        }

        public Task UpdateViewerCountsAsync(IReadOnlyDictionary<int, int> viewerCountsByChannel)
        {
            foreach (var (channelId, count) in viewerCountsByChannel)
            {
                if (_db.Channels.TryGetValue(channelId, out var c) && c.IsLive) _db.PersistedViewerCounts[channelId] = count;
            }
            return Task.CompletedTask;
        }

        public Task<IEnumerable<int>> GetLiveChannelIdsAsync() =>
            Task.FromResult<IEnumerable<int>>(_db.Channels.Values.Where(c => c.IsLive).Select(c => c.Id).ToList());

        public Task UpdateStreamKeyAsync(int channelId, string streamKey)
        {
            _db.Channels[channelId].StreamKey = streamKey;
            return Task.CompletedTask;
        }

        public Task<Channel?> GetByUserIdAsync(string userId)
        {
            var c = _db.Channels.Values.FirstOrDefault(x => x.UserId == userId);
            return Task.FromResult(c == null ? null : ToChannel(c));
        }

        public Task<Channel?> GetByIdAsync(int channelId) =>
            Task.FromResult(_db.Channels.TryGetValue(channelId, out var c) ? ToChannel(c) : null);

        public Task<IEnumerable<Channel>> GetLiveChannelsAsync() =>
            Task.FromResult<IEnumerable<Channel>>(_db.Channels.Values.Where(c => c.IsLive).Select(ToChannel).ToList());

        public Task<IEnumerable<ChannelSummary>> GetChannelSummariesAsync() =>
            Task.FromResult<IEnumerable<ChannelSummary>>(_db.Channels.Values.OrderBy(c => c.Id).Select(c => new ChannelSummary
            {
                Id = c.Id,
                Name = c.Name,
                UserId = c.UserId,
                IsLive = c.IsLive,
                ViewerCount = _db.PersistedViewerCounts.GetValueOrDefault(c.Id),
                OwnerDisplayName = c.OwnerDisplayName,
                OwnerLockoutEnd = c.OwnerLockoutEnd,
                CategoryName = c.CategoryName
            }).ToList());

        // Las pruebas no usan estas operaciones del CRUD de canales.
        public Task<IEnumerable<Channel>> GetByategoryAsync(int categoryId) => throw new NotSupportedException();
        public Task CreateAsync(Channel channel) => throw new NotSupportedException();
        public Task UpdateAsync(Channel channel) => throw new NotSupportedException();

        private Channel ToChannel(ChannelStreamInfo c) => new()
        {
            Id = c.Id,
            UserId = c.UserId,
            Name = c.Name,
            StreamKey = c.StreamKey,
            IsLive = c.IsLive,
            ViewerCount = _db.PersistedViewerCounts.GetValueOrDefault(c.Id),
            Emotes = _db.Emotes.TryGetValue(c.Id, out var emotes) ? emotes.ToList() : new List<Emote>()
        };
    }
}
