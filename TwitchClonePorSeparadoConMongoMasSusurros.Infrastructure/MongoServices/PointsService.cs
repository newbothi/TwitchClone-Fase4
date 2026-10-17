using Microsoft.EntityFrameworkCore;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public class PointsService
    {
        private readonly AppDbContext _db;

        public PointsService(AppDbContext db)
        {
            _db = db;
        }

        // Obtener puntos de un usuario
        public async Task<int> GetPointsAsync(string userId)
        {
            var user = await _db.Users.FindAsync(userId);
            return user?.Points ?? 0;
        }

        // Agregar puntos a un usuario
        public async Task<bool> AddPointsAsync(string userId, int amount)
        {
            var user = await _db.Users.FindAsync(userId);
            if (user == null) return false;

            user.Points += amount;
            await _db.SaveChangesAsync();
            return true;
        }

        // Procesar donación: descuenta puntos del donador, acredita al streamer, guarda en SQL
        public async Task<bool> ProcessDonationAsync(string fromUserId, int channelId, int points, string message)
        {
            var donor = await _db.Users.FindAsync(fromUserId);
            if (donor == null || donor.Points < points) return false;

            var channel = await _db.Channels
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.Id == channelId);

            if (channel == null || channel.User == null) return false;

            // Descontar puntos del donador
            donor.Points -= points;

            // Acreditar puntos al streamer
            channel.User.Points += points;

            // Guardar donación en SQL
            var donation = new Donation
            {
                FromUserId = fromUserId,
                ChannelId = channelId,
                Amount = points,
                Message = message ?? string.Empty,
                CreatedAt = DateTime.Now
            };

            await _db.Donations.AddAsync(donation);
            await _db.SaveChangesAsync();

            return true;
        }

        // Historial de donaciones de un canal desde SQL
        public async Task<List<Donation>> GetChannelDonationsAsync(int channelId, int count = 20)
        {
            return await _db.Donations
                .Include(d => d.FromUser)
                .Where(d => d.ChannelId == channelId)
                .OrderByDescending(d => d.CreatedAt)
                .Take(count)
                .ToListAsync();
        }
    }
}