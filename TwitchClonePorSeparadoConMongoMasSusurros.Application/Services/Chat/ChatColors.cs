namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat
{
    // Color de nombre por usuario, estable entre reinicios (no usa string.GetHashCode,
    // que cambia en cada ejecución de .NET). Paleta pensada para fondo oscuro.
    // En la Fase 2 el usuario podrá elegir su color con /color.
    public static class ChatColors
    {
        private static readonly string[] Palette =
        {
            "#FF4A4A", "#4A9EFF", "#3FCF6A", "#FF8A3D", "#B07CFF", "#FF5FA2",
            "#2EC4B6", "#F2C230", "#7FD1FF", "#9BE15D", "#FF7B7B", "#C792EA"
        };

        public static string ForUser(string userId)
        {
            // FNV-1a de 32 bits
            unchecked
            {
                uint hash = 2166136261;
                foreach (var c in userId)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return Palette[hash % (uint)Palette.Length];
            }
        }
    }
}
