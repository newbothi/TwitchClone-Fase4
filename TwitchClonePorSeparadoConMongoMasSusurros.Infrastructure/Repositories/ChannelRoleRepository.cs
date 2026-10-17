using Dapper;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Repositories
{
    // Moderadores y VIP por canal. Tabla ChannelRoles creada por SqlSchemaInitializer
    // (o por el script sql/fase2_channel_roles.sql). Un rol por usuario y canal.
    public class ChannelRoleRepository : IChannelRoleRepository
    {
        private readonly DbConnectionFactory _connectionFactory;

        public ChannelRoleRepository(DbConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<Dictionary<string, ChannelRoleType>> GetRolesAsync(int channelId)
        {
            using var connection = _connectionFactory.CreateConnection();
            var rows = await connection.QueryAsync<(string UserId, int Role)>(
                "SELECT UserId, CAST(Role AS INT) AS Role FROM ChannelRoles WHERE ChannelId = @ChannelId",
                new { ChannelId = channelId });
            return rows.ToDictionary(r => r.UserId, r => (ChannelRoleType)r.Role);
        }

        public async Task<List<ChannelRoleEntry>> ListAsync(int channelId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = """
                SELECT r.ChannelId, r.UserId, u.DisplayUsername AS DisplayName, CAST(r.Role AS INT) AS Role, r.CreatedAt
                FROM ChannelRoles r
                INNER JOIN Users u ON u.Id = r.UserId
                WHERE r.ChannelId = @ChannelId
                ORDER BY r.Role DESC, u.DisplayUsername
            """;
            return (await connection.QueryAsync<ChannelRoleEntry>(sql, new { ChannelId = channelId })).ToList();
        }

        public async Task SetRoleAsync(int channelId, string userId, ChannelRoleType role, string grantedById)
        {
            using var connection = _connectionFactory.CreateConnection();
            if (role == ChannelRoleType.None)
            {
                await connection.ExecuteAsync(
                    "DELETE FROM ChannelRoles WHERE ChannelId = @ChannelId AND UserId = @UserId",
                    new { ChannelId = channelId, UserId = userId });
                return;
            }

            const string sql = """
                MERGE ChannelRoles AS target
                USING (SELECT @ChannelId AS ChannelId, @UserId AS UserId) AS source
                   ON target.ChannelId = source.ChannelId AND target.UserId = source.UserId
                WHEN MATCHED THEN
                    UPDATE SET Role = @Role, GrantedById = @GrantedById, CreatedAt = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT (ChannelId, UserId, Role, GrantedById, CreatedAt)
                    VALUES (@ChannelId, @UserId, @Role, @GrantedById, SYSUTCDATETIME());
            """;
            await connection.ExecuteAsync(sql, new { ChannelId = channelId, UserId = userId, Role = (int)role, GrantedById = grantedById });
        }
    }
}
