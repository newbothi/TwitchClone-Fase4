using System.ComponentModel.DataAnnotations;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs
{
    public class EditProfileDto
    {
        [Required(ErrorMessage = "El nombre público es obligatorio")]
        [MaxLength(50, ErrorMessage = "Máximo 50 caracteres")]
        public string DisplayUsername { get; set; } = string.Empty;

        [MaxLength(500, ErrorMessage = "Máximo 500 caracteres")]
        public string? Bio { get; set; }
    }
}
