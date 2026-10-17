namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs
{
    // Fase 4 (c): lo que grafica el Dashboard. Sale del esquema analytics.* de SQL Server,
    // que llena el servicio Python (python/analytics). La app de C# solo lo lee.

    public class ChannelAnalyticsDto
    {
        // false = el esquema analytics todavía no existe (nunca se inició python/analytics).
        public bool Available { get; set; }
        public string? Message { get; set; }

        public int Days { get; set; }
        public string From { get; set; } = string.Empty;   // yyyy-MM-dd (hora local)
        public string To { get; set; } = string.Empty;

        // Última vez que el ETL terminó bien, y el último error (si lo hubo).
        public DateTime? LastUpdatedUtc { get; set; }
        public string? LastError { get; set; }

        public AnalyticsKpisDto Kpis { get; set; } = new();
        public List<AnalyticsDayDto> Daily { get; set; } = new();
        public List<AnalyticsViewerPointDto> Viewers { get; set; } = new();   // últimas 24 h
        public List<AnalyticsHourDto> Hours { get; set; } = new();            // 0 a 23, hora local
        public AnalyticsTrendDto? Trend { get; set; }
    }

    public class AnalyticsKpisDto
    {
        public int TotalFollowers { get; set; }
        public int NewFollowers { get; set; }
        public double? FollowersGrowthPct { get; set; }   // vs. el período anterior (null = antes no había)
        public double AvgViewers { get; set; }            // promedio de los días con stream
        public double? AvgViewersGrowthPct { get; set; }
        public int PeakViewers { get; set; }
        public double HoursLive { get; set; }
        public double HoursWatched { get; set; }
        public int ChatMessages { get; set; }
        public double MessagesPerHourLive { get; set; }
    }

    public class AnalyticsDayDto
    {
        public string Day { get; set; } = string.Empty;   // yyyy-MM-dd
        public int NewFollowers { get; set; }
        public int TotalFollowers { get; set; }
        public int MinutesLive { get; set; }
        public double AvgViewers { get; set; }
        public int PeakViewers { get; set; }
        public int ChatMessages { get; set; }
        public int UniqueChatters { get; set; }
    }

    public class AnalyticsViewerPointDto
    {
        public DateTime TimeUtc { get; set; }
        public double AvgViewers { get; set; }
        public int PeakViewers { get; set; }
    }

    public class AnalyticsHourDto
    {
        public int Hour { get; set; }
        public double AvgViewers { get; set; }
        public int ChatMessages { get; set; }
    }

    public class AnalyticsTrendDto
    {
        public int WindowDays { get; set; }
        public double FollowersPerDay { get; set; }
        public double FollowersR2 { get; set; }
        public int ProjectedFollowers7d { get; set; }
        public double? FollowersGrowthPct { get; set; }
        public double AvgViewersPerDay { get; set; }
        public double? ViewersGrowthPct { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    // Fila de analytics.ChannelDaily tal como la lee Dapper.
    public class AnalyticsDailyRow
    {
        public DateTime Day { get; set; }
        public int NewFollowers { get; set; }
        public int TotalFollowers { get; set; }
        public int MinutesLive { get; set; }
        public decimal AvgViewers { get; set; }
        public int PeakViewers { get; set; }
        public long ViewerMinutes { get; set; }
        public int ChatMessages { get; set; }
        public int UniqueChatters { get; set; }
    }

    public record AnalyticsEtlStatus(DateTime? LastOkUtc, string? LastError);
}
