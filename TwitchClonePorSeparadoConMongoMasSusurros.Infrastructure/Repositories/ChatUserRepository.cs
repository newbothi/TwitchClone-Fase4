using Dapper;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Repositories
{
    // Consultas livianas sobre Users/Follows que necesita el chat (Dapper).
    public class ChatUserRepository : IChatUserRepository
    {
        private readonly DbConnectionFactory _connectionFactory;

        public ChatUserRepository(DbConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<ChatUserLookup?> FindByDisplayNameAsync(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return null;
            using var connection = _connectionFactory.CreateConnection();
            // DisplayUsername no es único en la base: si hay repetidos, gana el más antiguo.
            return await connection.QueryFirstOrDefaultAsync<ChatUserLookup>(
                "SELECT TOP 1 Id, DisplayUsername FROM Users WHERE DisplayUsername = @Name ORDER BY CreatedAt",
                new { Name = displayName.Trim() });
        }

        public async Task<DateTime?> GetFollowDateAsync(string followerId, string followedId)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.QueryFirstOrDefaultAsync<DateTime?>(
                "SELECT TOP 1 CreatedAt FROM Follows WHERE FollowerId = @FollowerId AND FollowedId = @FollowedId",
                new { FollowerId = followerId, FollowedId = followedId });
        }

        public async Task<int> GetPointsAsync(string userId)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.QueryFirstOrDefaultAsync<int>(
                "SELECT Points FROM Users WHERE Id = @Id", new { Id = userId });
        }

        public async Task AddPointsAsync(IReadOnlyCollection<string> userIds, int amount)
        {
            if (userIds.Count == 0 || amount == 0) return;
            using var connection = _connectionFactory.CreateConnection();
            // Dapper expande la lista en IN (@p1, @p2, ...). En lotes de 1000 (límite de parámetros de SQL Server es 2100).
            foreach (var chunk in userIds.Chunk(1000))
            {
                await connection.ExecuteAsync(
                    "UPDATE Users SET Points = Points + @Amount WHERE Id IN @Ids",
                    new { Amount = amount, Ids = chunk });
            }
        }
    
        public async Task<bool> TryDonatePointsAsync(string fromUserId, int channelId, int amount, string message)
        {
            if (amount <= 0) return false;
            using var connection = _connectionFactory.CreateConnection();
            // El UPDATE con "Points >= @Amount" es atómico: dos donaciones simultáneas
            // no pueden gastar los mismos puntos.
            const string sql = """
                SET XACT_ABORT ON;
                BEGIN TRAN;
                UPDATE Users SET Points = Points - @Amount WHERE Id = @UserId AND Points >= @Amount;
                IF @@ROWCOUNT = 1
                BEGIN
                    INSERT INTO Donations (FromUserId, ChannelId, Amount, Message, CreatedAt)
                    VALUES (@UserId, @ChannelId, @Amount, @Message, GETDATE());
                    COMMIT;
                    SELECT CAST(1 AS BIT);
                END
                ELSE
                BEGIN
                    ROLLBACK;
                    SELECT CAST(0 AS BIT);
                END
            """;
            return await connection.ExecuteScalarAsync<bool>(sql, new
            {
                UserId = fromUserId,
                ChannelId = channelId,
                Amount = amount,
                Message = (message ?? string.Empty).Length > 500 ? message![..500] : message ?? string.Empty
            });
        }
    }
}
