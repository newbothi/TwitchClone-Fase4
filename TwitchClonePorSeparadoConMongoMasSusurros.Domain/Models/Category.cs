using System.ComponentModel.DataAnnotations;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    public class Category
    {
        public int Id { get; set; }

        [Required, MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(500)]
        public string Description { get; set; } = string.Empty;

        [MaxLength(200)]
        public string ImageUrl { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Categoría padre (ej. "Street Fighter 6" tiene como padre a "Videojuegos").
        // Null = categoría raíz, la que se ofrece al registrarse como Streamer.
        public int? ParentCategoryId { get; set; }

        // Navegación
        public ICollection<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Channel> Channels { get; set; } = new List<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Channel>();
        public ICollection<CategoryTag> CategoryTags { get; set; } = new List<CategoryTag>();
        public Category? ParentCategory { get; set; }
        public ICollection<Category> SubCategories { get; set; } = new List<Category>();
    }
}