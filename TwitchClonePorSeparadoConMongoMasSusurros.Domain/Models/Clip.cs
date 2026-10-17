using System.ComponentModel.DataAnnotations;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    public class Clip
    {
        public int Id { get; set; }

        public int ChannelId { get; set; }
        public string CreatedByUserId { get; set; } = string.Empty;

        [Required, MaxLength(200)]
        public string Title { get; set; } = string.Empty;

        [Required, MaxLength(500)]
        public string VideoUrl { get; set; } = string.Empty;

        public int DurationSeconds { get; set; } = 0;
        public int Views { get; set; } = 0;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Channel? Channel { get; set; }
        public TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.AppUser? CreatedBy { get; set; }
    }
}