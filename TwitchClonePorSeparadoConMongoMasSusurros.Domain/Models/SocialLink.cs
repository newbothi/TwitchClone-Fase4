using System.ComponentModel.DataAnnotations;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    public class SocialLink
    {
        public int Id { get; set; }

        [Required] public string UserId { get; set; } = string.Empty;

        [Required, MaxLength(50)]
        public string Title { get; set; } = string.Empty;

        [Required, MaxLength(300)]
        public string Url { get; set; } = string.Empty;

        // Orden de aparición en el perfil (0 = primero)
        public int SortOrder { get; set; } = 0;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Navegación
        public AppUser? User { get; set; }
    }
}
