namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    // Ciclo de vida de una transmisión: quién puede publicar y qué pasa cuando
    // el stream aparece o desaparece en el servidor de medios (MediaMTX).
    public interface IStreamLifecycleService
    {
        // Valida la clave de transmisión que mandó OBS (query "?key=...").
        Task<PublishAuthorization> AuthorizePublishAsync(int channelId, string? providedKey);

        // Marca el canal EN VIVO. Changed=false si ya lo estaba (webhook repetido).
        Task<StreamStateChange?> MarkLiveAsync(int channelId);

        // Marca el canal OFFLINE y cierra métricas/sesión.
        Task<StreamStateChange?> MarkOfflineAsync(int channelId);
    }

    public enum PublishAuthorization
    {
        Allowed,
        ChannelNotFound,
        MissingKey,
        InvalidKey,
        OwnerSuspended
    }

    public record StreamStateChange(int ChannelId, bool Changed, string ChannelName, string OwnerDisplayName);
}
