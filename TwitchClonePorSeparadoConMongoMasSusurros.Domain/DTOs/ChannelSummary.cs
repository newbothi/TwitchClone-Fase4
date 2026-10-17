namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs
{
    // Fila del panel del simulador (y de cualquier listado liviano de canales).
    public class ChannelSummary
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public bool IsLive { get; set; }
        public int ViewerCount { get; set; }
        public string OwnerDisplayName { get; set; } = string.Empty;
        public DateTimeOffset? OwnerLockoutEnd { get; set; }
        public string? CategoryName { get; set; }

        public bool OwnerIsSuspended => OwnerLockoutEnd.HasValue && OwnerLockoutEnd.Value > DateTimeOffset.UtcNow;
    }
}
