using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    public class AppUser : IdentityUser
    {
        public string DisplayUsername { get; set; } = string.Empty;
        public int Points { get; set; } = 0;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [MaxLength(300)] public string? AvatarUrl { get; set; }
        [MaxLength(300)] public string? BannerUrl { get; set; }
        [MaxLength(500)] public string? Bio { get; set; }

        // Navegación (1:1 con Channel)
        public TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Channel? Channel { get; set; }
        public ICollection<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Message> Messages { get; set; } = new List<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Message>();
        public ICollection<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Donation> DonationsSent { get; set; } = new List<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Donation>();
        public ICollection<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.SocialLink> SocialLinks { get; set; } = new List<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.SocialLink>();


        public ICollection<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Follow> Followers { get; set; } = new List<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Follow>();
        //public ICollection<ICollection<TwitchClonePorSeparadoSinMongo.Domain.Models.Follow> Following { get; set; } = new List<ICollection<TwitchClonePorSeparadoSinMongo.Domain.Models.Follow>();

        public ICollection<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Follow> Following { get; set; } = new List<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Follow>();

    }
}