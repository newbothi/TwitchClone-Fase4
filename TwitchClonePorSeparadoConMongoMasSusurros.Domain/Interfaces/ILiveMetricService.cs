using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    public interface ILiveMetricService
    {
        Task StartStreamAsync(string channelId);
        Task StopStreamAsync(string channelId);
        Task IncrementViewersAsync(string channelId);
        Task DecrementViewersAsync(string channelId);

        // Fija el número actual de viewers y sube el pico si corresponde.
        Task SetViewerCountAsync(string channelId, int viewers);

        Task<LiveMetric?> GetMetricAsync(string channelId);
        Task<List<LiveMetric>> GetLiveChannelsAsync();
    }
}
