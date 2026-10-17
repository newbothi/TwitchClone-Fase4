using System.ComponentModel.DataAnnotations;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    public class BannedUser
    {
        public int Id { get; set; }

        [Required]
        public string UserId { get; set; } = string.Empty;

        [Required]
        public int ChannelId { get; set; }

        [Required]
        public string BannedById { get; set; } = string.Empty;

        [MaxLength(500)]
        public string Reason { get; set; } = string.Empty;

        public DateTime? ExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Navegación
        public AppUser? User { get; set; }
        public AppUser? BannedBy { get; set; }
        public Channel? Channel { get; set; }
    }
}