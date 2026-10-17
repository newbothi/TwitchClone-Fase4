using Dapper;
using Microsoft.EntityFrameworkCore;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;



namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Repositories
{
    public class ChannelRepository : IChannelRepository
    {
        private readonly AppDbContext _context;
        private readonly DbConnectionFactory _connectionFactory;

        public ChannelRepository(AppDbContext context, DbConnectionFactory connectionFactory)
        {
            _context = context;
            _connectionFactory = connectionFactory;
        }

        public async Task<Channel?> GetByUserIdAsync(string userId)
        {
            return await _context.Channels
                .Include(c => c.Category)
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.UserId == userId);
        }

        public async Task<Channel?> GetByIdAsync(int channelId)
        {
            return await _context.Channels
                .Include(c => c.Category)
                .Include(c => c.User)
                .Include(c => c.Emotes)
                .FirstOrDefaultAsync(c => c.Id == channelId);
        }

        public async Task<IEnumerable<Channel>> GetLiveChannelsAsync()
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = """
                SELECT c.Id, c.UserId, c.Name, c.Description,
                       c.IsLive, c.ViewerCount, c.CategoryId, c.CreatedAt
                FROM Channels c
                WHERE c.IsLive = 1
                ORDER BY c.ViewerCount DESC
            """;
            return await connection.QueryAsync<Channel>(sql);
        }

        public async Task<IEnumerable<Channel>> GetByategoryAsync(int categoryId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = """
                SELECT c.Id, c.UserId, c.Name, c.Description,
                       c.IsLive, c.ViewerCount, c.CategoryId, c.CreatedAt
                FROM Channels c
                WHERE c.CategoryId = @CategoryId
                ORDER BY c.IsLive DESC, c.ViewerCount DESC
            """;
            return await connection.QueryAsync<Channel>(sql, new { CategoryId = categoryId });
        }

        public async Task CreateAsync(Channel channel)
        {
            await _context.Channels.AddAsync(channel);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(Channel channel)
        {
            _context.Channels.Update(channel);
            await _context.SaveChangesAsync();
        }

        // ── Consultas livianas (Dapper) para el chat y el streaming ──────────

        public async Task<string?> GetOwnerUserIdAsync(int channelId)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.QueryFirstOrDefaultAsync<string>(
                "SELECT UserId FROM Channels WHERE Id = @Id", new { Id = channelId });
        }

        public async Task<IEnumerable<Emote>> GetEmotesAsync(int channelId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = """
                SELECT Id, ChannelId, Code, ImageUrl, CreatedAt
                FROM Emotes
                WHERE ChannelId = @ChannelId
            """;
            return await connection.QueryAsync<Emote>(sql, new { ChannelId = channelId });
        }

        public async Task<ChannelStreamInfo?> GetStreamInfoAsync(int channelId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = """
                SELECT c.Id, c.UserId, c.Name, c.StreamKey, c.IsLive,
                       u.DisplayUsername AS OwnerDisplayName,
                       u.LockoutEnd      AS OwnerLockoutEnd,
                       cat.Name          AS CategoryName
                FROM Channels c
                INNER JOIN Users u ON u.Id = c.UserId
                LEFT JOIN Categories cat ON cat.Id = c.CategoryId
                WHERE c.Id = @Id
            """;
            return await connection.QueryFirstOrDefaultAsync<ChannelStreamInfo>(sql, new { Id = channelId });
        }

        public async Task<bool> SetLiveStateAsync(int channelId, bool isLive)
        {
            using var connection = _connectionFactory.CreateConnection();
            // Solo toca la fila si el estado realmente cambia: así un webhook repetido
            // no vuelve a disparar notificaciones ni reinicia métricas.
            const string sql = """
                UPDATE Channels
                SET IsLive = @IsLive, ViewerCount = 0
                WHERE Id = @Id AND IsLive <> @IsLive
            """;
            var rows = await connection.ExecuteAsync(sql, new { Id = channelId, IsLive = isLive });
            return rows > 0;
        }

        public async Task UpdateViewerCountsAsync(IReadOnlyDictionary<int, int> viewerCountsByChannel)
        {
            if (viewerCountsByChannel.Count == 0) return;

            using var connection = _connectionFactory.CreateConnection();
            const string sql = "UPDATE Channels SET ViewerCount = @Count WHERE Id = @Id AND IsLive = 1";
            await connection.ExecuteAsync(sql,
                viewerCountsByChannel.Select(kv => new { Id = kv.Key, Count = kv.Value }));
        }

        public async Task<IEnumerable<int>> GetLiveChannelIdsAsync()
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.QueryAsync<int>("SELECT Id FROM Channels WHERE IsLive = 1");
        }

        public async Task<IEnumerable<ChannelSummary>> GetChannelSummariesAsync()
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = """
                SELECT c.Id, c.Name, c.UserId, c.IsLive, c.ViewerCount,
                       u.DisplayUsername AS OwnerDisplayName,
                       u.LockoutEnd      AS OwnerLockoutEnd,
                       cat.Name          AS CategoryName
                FROM Channels c
                INNER JOIN Users u ON u.Id = c.UserId
                LEFT JOIN Categories cat ON cat.Id = c.CategoryId
                ORDER BY c.Id
            """;
            return await connection.QueryAsync<ChannelSummary>(sql);
        }

        public async Task UpdateStreamKeyAsync(int channelId, string streamKey)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                "UPDATE Channels SET StreamKey = @StreamKey WHERE Id = @Id",
                new { Id = channelId, StreamKey = streamKey });
        }
    }
}

