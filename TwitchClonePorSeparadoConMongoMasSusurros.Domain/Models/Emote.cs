using System.ComponentModel.DataAnnotations;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    public class Emote
    {
        public int Id { get; set; }

        public int ChannelId { get; set; }

        [Required, MaxLength(50)]
        public string Code { get; set; } = string.Empty; // ej: :pepega:

        [Required, MaxLength(500)]
        public string ImageUrl { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Navegación
        public TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Channel? Channel { get; set; }
    }
}