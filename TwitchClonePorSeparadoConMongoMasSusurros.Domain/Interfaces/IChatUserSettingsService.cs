namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    // Preferencias de chat por usuario (colección "ChatUserSettings" en MongoDB).
    public interface IChatUserSettingsService
    {
        Task<string?> GetColorAsync(string userId);
        Task SetColorAsync(string userId, string? color);
    }
}
