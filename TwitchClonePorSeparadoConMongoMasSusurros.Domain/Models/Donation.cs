using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;


namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    public class Donation
    {
        public int Id { get; set; }

        [Required]
        public string FromUserId { get; set; } = string.Empty;

        [Required]
        public int ChannelId { get; set; }

        [Required]
        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        [MaxLength(500)]
        public string Message { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Navegación
        public TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.AppUser? FromUser { get; set; }
        public TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Channel? Channel { get; set; }
    }
}