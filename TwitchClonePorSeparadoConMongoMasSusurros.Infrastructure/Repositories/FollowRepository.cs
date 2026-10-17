using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;

using Dapper;
using Microsoft.EntityFrameworkCore;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;

using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;


namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Repositories
{
    public class FollowRepository : IFollowRepository
    {
        private readonly AppDbContext _context;
        private readonly DbConnectionFactory _connectionFactory;

        public FollowRepository(AppDbContext context, DbConnectionFactory connectionFactory)
        {
            _context = context;
            _connectionFactory = connectionFactory;
        }

        // Dapper — verificación rápida de follow
        public async Task<bool> IsFollowingAsync(string followerId, string followedId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = """
                SELECT COUNT(1) FROM Follows
                WHERE FollowerId = @FollowerId AND FollowedId = @FollowedId
            """;
            var count = await connection.ExecuteScalarAsync<int>(
                sql, new { FollowerId = followerId, FollowedId = followedId });
            return count > 0;
        }

        // Dapper — conteo de seguidores
        public async Task<int> GetFollowerCountAsync(string userId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = """
                SELECT COUNT(1) FROM Follows WHERE FollowedId = @UserId
            """;
            return await connection.ExecuteScalarAsync<int>(sql, new { UserId = userId });
        }

        // Dapper — conteo de seguidos
        public async Task<int> GetFollowingCountAsync(string userId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = """
                SELECT COUNT(1) FROM Follows WHERE FollowerId = @UserId
            """;
            return await connection.ExecuteScalarAsync<int>(sql, new { UserId = userId });
        }

        // Dapper — lista de seguidores con datos de usuario
        public async Task<IEnumerable<AppUser>> GetFollowersAsync(string userId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = """
                SELECT u.Id, u.UserName, u.Email, u.DisplayUsername
                FROM Users u
                INNER JOIN Follows f ON f.FollowerId = u.Id
                WHERE f.FollowedId = @UserId
                ORDER BY f.CreatedAt DESC
            """;
            return await connection.QueryAsync<AppUser>(sql, new { UserId = userId });
        }

        // Dapper — lista de usuarios que sigo
        public async Task<IEnumerable<AppUser>> GetFollowingAsync(string userId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = """
                SELECT u.Id, u.UserName, u.Email, u.DisplayUsername
                FROM Users u
                INNER JOIN Follows f ON f.FollowedId = u.Id
                WHERE f.FollowerId = @UserId
                ORDER BY f.CreatedAt DESC
            """;
            return await connection.QueryAsync<AppUser>(sql, new { UserId = userId });
        }

        // EF Core — crear follow
        public async Task FollowAsync(string followerId, string followedId)
        {
            var follow = new Follow
            {
                FollowerId = followerId,
                FollowedId = followedId,
                CreatedAt = DateTime.Now
            };
            await _context.Follows.AddAsync(follow);
            await _context.SaveChangesAsync();
        }

        // EF Core — eliminar follow
        public async Task UnfollowAsync(string followerId, string followedId)
        {
            var follow = await _context.Follows
                .FirstOrDefaultAsync(f =>
                    f.FollowerId == followerId &&
                    f.FollowedId == followedId);

            if (follow != null)
            {
                _context.Follows.Remove(follow);
                await _context.SaveChangesAsync();
            }
        }
    }
}

