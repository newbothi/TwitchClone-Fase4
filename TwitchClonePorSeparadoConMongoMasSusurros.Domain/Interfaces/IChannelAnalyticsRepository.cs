using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    // Fase 4 (c): lectura del esquema analytics.* (Dapper). Solo lectura.
    // Los días (fromDay/toDay) son fechas LOCALES, igual que las escribe python/analytics.
    public interface IChannelAnalyticsRepository
    {
        Task<bool> IsAvailableAsync();
        Task<List<AnalyticsDailyRow>> GetDailyAsync(int channelId, DateTime fromDay, DateTime toDay);
        Task<List<AnalyticsViewerPointDto>> GetViewerSeriesAsync(int channelId, DateTime fromUtc, int bucketMinutes);
        Task<List<AnalyticsHourDto>> GetHoursAsync(int channelId, DateTime fromDay, DateTime toDay);
        Task<AnalyticsTrendDto?> GetTrendAsync(int channelId);
        Task<AnalyticsEtlStatus> GetEtlStatusAsync();
    }

    public interface IChannelAnalyticsService
    {
        Task<ChannelAnalyticsDto> GetAsync(int channelId, int days);
    }
}
