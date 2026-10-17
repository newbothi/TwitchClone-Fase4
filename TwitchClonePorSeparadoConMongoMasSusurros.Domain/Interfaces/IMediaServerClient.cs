namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    // Cliente de la API de control de MediaMTX (http://127.0.0.1:9997 por defecto).
    public interface IMediaServerClient
    {
        // Paths que hoy tienen un stream disponible (ej. "live/12").
        // Devuelve null si la API no responde (MediaMTX apagado o API deshabilitada).
        Task<IReadOnlySet<string>?> GetAvailablePathsAsync(CancellationToken ct = default);

        // Desconecta a quien esté publicando en ese path (OBS). true si se pudo.
        Task<bool> KickPublisherAsync(string path, CancellationToken ct = default);
    }
}
