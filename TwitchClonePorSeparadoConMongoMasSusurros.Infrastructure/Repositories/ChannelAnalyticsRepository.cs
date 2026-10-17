using System.Data;
using Dapper;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Repositories
{
    // Fase 4 (c): lee el esquema analytics.* con Dapper. Solo lectura: lo escribe python/analytics.
    // Los DECIMAL se convierten a float en SQL para que Dapper los mapee directo a double.
    public class ChannelAnalyticsRepository : IChannelAnalyticsRepository
    {
        private readonly DbConnectionFactory _connections;

        public ChannelAnalyticsRepository(DbConnectionFactory connections) => _connections = connections;

        public async Task<bool> IsAvailableAsync()
        {
            using var connection = _connections.CreateConnection();
            return await connection.ExecuteScalarAsync<int>(
                "SELECT CASE WHEN OBJECT_ID(N'analytics.ChannelDaily', N'U') IS NULL THEN 0 ELSE 1 END") == 1;
        }

        public async Task<List<AnalyticsDailyRow>> GetDailyAsync(int channelId, DateTime fromDay, DateTime toDay)
        {
            using var connection = _connections.CreateConnection();
            var rows = await connection.QueryAsync<AnalyticsDailyRow>(
                """
                SELECT [Day], NewFollowers, TotalFollowers, MinutesLive, AvgViewers, PeakViewers,
                       ViewerMinutes, ChatMessages, UniqueChatters
                FROM analytics.ChannelDaily
                WHERE ChannelId = @ChannelId AND [Day] BETWEEN @FromDay AND @ToDay
                ORDER BY [Day]
                """,
                Days(channelId, fromDay, toDay));
            return rows.ToList();
        }

        public async Task<List<AnalyticsViewerPointDto>> GetViewerSeriesAsync(int channelId, DateTime fromUtc, int bucketMinutes)
        {
            using var connection = _connections.CreateConnection();
            var points = await connection.QueryAsync<AnalyticsViewerPointDto>(
                """
                SELECT b.TimeUtc,
                       CAST(AVG(CAST(b.Viewers AS float)) AS float) AS AvgViewers,
                       MAX(b.Viewers) AS PeakViewers
                FROM (
                    SELECT DATEADD(MINUTE, (DATEDIFF(MINUTE, '20000101', CapturedAtUtc) / @Bucket) * @Bucket,
                                   CAST('20000101' AS datetime2(0))) AS TimeUtc,
                           Viewers
                    FROM analytics.ViewerSnapshots
                    WHERE ChannelId = @ChannelId AND CapturedAtUtc >= @FromUtc
                ) AS b
                GROUP BY b.TimeUtc
                ORDER BY b.TimeUtc
                """,
                new { ChannelId = channelId, FromUtc = fromUtc, Bucket = Math.Max(1, bucketMinutes) });
            return points.Select(p => { p.TimeUtc = DateTime.SpecifyKind(p.TimeUtc, DateTimeKind.Utc); return p; }).ToList();
        }

        public async Task<List<AnalyticsHourDto>> GetHoursAsync(int channelId, DateTime fromDay, DateTime toDay)
        {
            using var connection = _connections.CreateConnection();
            var hours = await connection.QueryAsync<AnalyticsHourDto>(
                """
                SELECT CAST([Hour] AS int) AS [Hour],
                       CAST(ISNULL(AVG(CASE WHEN Samples > 0 THEN CAST(AvgViewers AS float) END), 0) AS float) AS AvgViewers,
                       SUM(ChatMessages) AS ChatMessages
                FROM analytics.ChannelHourly
                WHERE ChannelId = @ChannelId AND [Day] BETWEEN @FromDay AND @ToDay
                GROUP BY [Hour]
                ORDER BY [Hour]
                """,
                Days(channelId, fromDay, toDay));
            return hours.ToList();
        }

        public async Task<AnalyticsTrendDto?> GetTrendAsync(int channelId)
        {
            using var connection = _connections.CreateConnection();
            var trend = await connection.QuerySingleOrDefaultAsync<AnalyticsTrendDto>(
                """
                SELECT WindowDays,
                       CAST(FollowersPerDay AS float)    AS FollowersPerDay,
                       CAST(FollowersR2 AS float)        AS FollowersR2,
                       ProjectedFollowers7d,
                       CAST(FollowersGrowthPct AS float) AS FollowersGrowthPct,
                       CAST(AvgViewersPerDay AS float)   AS AvgViewersPerDay,
                       CAST(ViewersGrowthPct AS float)   AS ViewersGrowthPct,
                       UpdatedAtUtc
                FROM analytics.ChannelTrends
                WHERE ChannelId = @ChannelId
                """,
                new { ChannelId = channelId });
            if (trend != null) trend.UpdatedAtUtc = DateTime.SpecifyKind(trend.UpdatedAtUtc, DateTimeKind.Utc);
            return trend;
        }

        public async Task<AnalyticsEtlStatus> GetEtlStatusAsync()
        {
            using var connection = _connections.CreateConnection();
            var row = await connection.QuerySingleAsync<(DateTime? LastOkUtc, string? LastError)>(
                """
                SELECT (SELECT MAX(FinishedAtUtc) FROM analytics.EtlRuns WHERE Ok = 1 AND Job = N'aggregate') AS LastOkUtc,
                       (SELECT TOP (1) Error FROM analytics.EtlRuns WHERE FinishedAtUtc IS NOT NULL ORDER BY Id DESC) AS LastError
                """);
            return new AnalyticsEtlStatus(
                row.LastOkUtc.HasValue ? DateTime.SpecifyKind(row.LastOkUtc.Value, DateTimeKind.Utc) : null,
                row.LastError);
        }

        private static DynamicParameters Days(int channelId, DateTime fromDay, DateTime toDay)
        {
            var parameters = new DynamicParameters();
            parameters.Add("ChannelId", channelId);
            parameters.Add("FromDay", fromDay.Date, DbType.Date);
            parameters.Add("ToDay", toDay.Date, DbType.Date);
            return parameters;
        }
    }
}
