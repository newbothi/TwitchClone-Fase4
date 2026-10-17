using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes
{
    public sealed class InMemoryUserRepository : IUserRepository
    {
        private readonly InMemoryPlatform _db;

        public InMemoryUserRepository(InMemoryPlatform db) => _db = db;

        public Task<AppUser?> GetByIdAsync(string userId) =>
            Task.FromResult(_db.Users.TryGetValue(userId, out var u) ? u : null);

        public Task<AppUser?> GetByEmailAsync(string email) =>
            Task.FromResult(_db.Users.Values.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)));

        public Task<IEnumerable<AppUser>> SearchUsersAsync(string query) =>
            Task.FromResult<IEnumerable<AppUser>>(_db.Users.Values
                .Where(u => u.DisplayUsername.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList());

        public Task UpdateAsync(AppUser user)
        {
            _db.Users[user.Id] = user;
            return Task.CompletedTask;
        }
    }
}
