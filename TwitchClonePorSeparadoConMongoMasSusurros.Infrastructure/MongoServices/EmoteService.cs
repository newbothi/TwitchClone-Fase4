//using TwitchCloneMasMongo.Data;
//using TwitchCloneMasMongo.Models;

using Microsoft.EntityFrameworkCore;

using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
//using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices;


namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public class EmoteService
    {
        private readonly AppDbContext _db;

        public EmoteService(AppDbContext db)
        {
            _db = db;
        }

        public async Task<List<Emote>> GetEmotesByChannelAsync(int channelId)
        {
            return await _db.Emotes
                .Where(e => e.ChannelId == channelId)
                .OrderBy(e => e.Code)
                .ToListAsync();
        }

        public async Task<Emote?> CreateEmoteAsync(int channelId, string code, string imageUrl)
        {
            var exists = await _db.Emotes
                .AnyAsync(e => e.ChannelId == channelId && e.Code == code);
            if (exists) return null;

            var emote = new Emote
            {
                ChannelId = channelId,
                Code = code,
                ImageUrl = imageUrl,
                CreatedAt = DateTime.Now
            };
            _db.Emotes.Add(emote);
            await _db.SaveChangesAsync();
            return emote;
        }

        public async Task<Emote?> GetEmoteByIdAsync(int id)
        {
            return await _db.Emotes.FirstOrDefaultAsync(e => e.Id == id);
        }

        public async Task<bool> DeleteEmoteAsync(int id)
        {
            var emote = await _db.Emotes.FindAsync(id);
            if (emote == null) return false;
            _db.Emotes.Remove(emote);
            await _db.SaveChangesAsync();
            return true;
        }
    }
}