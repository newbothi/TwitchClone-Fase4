namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    // Tabla de unión explícita para la relación N:M entre Category y Tag.
    // Ej: la categoría "Street Fighter 6" está asociada a los tags "Lucha" y "Arcade".
    public class CategoryTag
    {
        public int Id { get; set; }

        public int CategoryId { get; set; }
        public int TagId { get; set; }

        // Navegación
        public Category? Category { get; set; }
        public Tag? Tag { get; set; }
    }
}
