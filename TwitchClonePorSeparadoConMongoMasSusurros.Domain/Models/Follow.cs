using System.ComponentModel.DataAnnotations;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    public class Follow
    {
        public int Id { get; set; }

        [Required]
        public string FollowerId { get; set; } = string.Empty;

        [Required]
        public string FollowedId { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Navegación
        public TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.AppUser? Follower { get; set; }
        public TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.AppUser? Followed { get; set; }
    }
}