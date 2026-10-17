namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs
{
    // Fila mínima (Channels + Users + Categories) para validar publicaciones y
    // manejar el ciclo de vida de un stream sin cargar todo el grafo de EF.
    public class ChannelStreamInfo
    {
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string StreamKey { get; set; } = string.Empty;
        public bool IsLive { get; set; }
        public string OwnerDisplayName { get; set; } = string.Empty;
        public DateTimeOffset? OwnerLockoutEnd { get; set; }
        public string? CategoryName { get; set; }

        public bool OwnerIsSuspended => OwnerLockoutEnd.HasValue && OwnerLockoutEnd.Value > DateTimeOffset.UtcNow;
    }
}
