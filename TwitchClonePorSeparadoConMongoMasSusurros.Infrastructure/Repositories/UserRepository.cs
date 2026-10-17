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
    public class UserRepository : IUserRepository
    {
        private readonly AppDbContext _context;
        private readonly DbConnectionFactory _connectionFactory;

        public UserRepository(AppDbContext context, DbConnectionFactory connectionFactory)
        {
            _context = context;
            _connectionFactory = connectionFactory;
        }

        // EF Core — buscar por Id con canal incluido
        public async Task<AppUser?> GetByIdAsync(string userId)
        {
            return await _context.Users
                .Include(u => u.Channel)
                .FirstOrDefaultAsync(u => u.Id == userId);
        }

        // EF Core — buscar por Email con canal incluido
        public async Task<AppUser?> GetByEmailAsync(string email)
        {
            return await _context.Users
                .Include(u => u.Channel)
                .FirstOrDefaultAsync(u => u.Email == email);
        }

        // Dapper — búsqueda por texto libre, más eficiente con LIKE
        public async Task<IEnumerable<AppUser>> SearchUsersAsync(string query)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = """
                SELECT Id, UserName, Email, DisplayUsername
                FROM Users
                WHERE DisplayUsername LIKE @Query
                   OR Email LIKE @Query
                ORDER BY DisplayUsername
            """;
            return await connection.QueryAsync<AppUser>(
                sql, new { Query = $"%{query}%" });
        }

        // EF Core — actualizar usuario
        public async Task UpdateAsync(AppUser user)
        {
            _context.Users.Update(user);
            await _context.SaveChangesAsync();
        }
    }
}
