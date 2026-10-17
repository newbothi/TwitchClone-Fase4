using System.ComponentModel.DataAnnotations;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    public class Message
    {
        public int Id { get; set; }

        public int ChannelId { get; set; }

        public string UserId { get; set; } = string.Empty;

        [Required, MaxLength(500)]
        public string Content { get; set; } = string.Empty;

        public DateTime SentAt { get; set; } = DateTime.Now;

        // Navegación
        public TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Channel? Channel { get; set; }
        public TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.AppUser? User { get; set; }
    }
}