using System.ComponentModel.DataAnnotations;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    public class Channel
    {
        public int Id { get; set; }
        [Required] public string UserId { get; set; } = string.Empty;
        [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
        [MaxLength(500)] public string Description { get; set; } = string.Empty;
        [MaxLength(200)] public string StreamKey { get; set; } = Guid.NewGuid().ToString();
        public bool IsLive { get; set; } = false;
        public int ViewerCount { get; set; } = 0;
        public int? CategoryId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;


        // Navegación
        public AppUser? User { get; set; }
        public Category? Category { get; set; }
        public ICollection<Emote> Emotes { get; set; } = new List<Emote>();
        public ICollection<Clip> Clips { get; set; } = new List<Clip>();
        public ICollection<BannedUser> BannedUsers { get; set; } = new List<BannedUser>();

        //public ICollection<TwitchClonePorSeparadoSinMongo.Domain.Models.Emote> Emotes { get; set; } = new List<TwitchClonePorSeparadoSinMongo.Domain.Models.Emote>();
        //public ICollection<TwitchClonePorSeparadoSinMongo.Domain.Models.Clip> Clips { get; set; } = new List<TwitchClonePorSeparadoSinMongo.Domain.Models.Clip>();
        //public ICollection<TwitchClonePorSeparadoSinMongo.Domain.Models.BannedUser> BannedUsers { get; set; } = new List<TwitchClonePorSeparadoSinMongo.Domain.Models.BannedUser>();
    }
}

