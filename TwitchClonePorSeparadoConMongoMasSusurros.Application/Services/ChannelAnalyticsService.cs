using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services
{
    // Fase 4 (c): arma lo que muestran los gráficos del Dashboard a partir de analytics.*.
    // No calcula tendencias (eso lo hace python/analytics y lo deja en analytics.ChannelTrends):
    // solo junta, rellena los días sin datos y compara con el período anterior.
    public class ChannelAnalyticsService : IChannelAnalyticsService
    {
        public static readonly int[] AllowedDays = { 7, 30, 90 };
        public const int ViewerSeriesHours = 24;
        public const int ViewerBucketMinutes = 5;

        // Si el ETL no termina bien en este tiempo, el Dashboard avisa que los datos están viejos.
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(20);

        private readonly IChannelAnalyticsRepository _repository;
        private readonly TimeProvider _time;

        public ChannelAnalyticsService(IChannelAnalyticsRepository repository, TimeProvider time)
        {
            _repository = repository;
            _time = time;
        }

        public async Task<ChannelAnalyticsDto> GetAsync(int channelId, int days)
        {
            days = AllowedDays.Contains(days) ? days : 30;
            // Mismo "hoy" que usa python/analytics (los dos corren en el mismo PC y zona horaria).
            var today = _time.GetLocalNow().Date;
            var from = today.AddDays(-(days - 1));
            var dto = new ChannelAnalyticsDto { Days = days, From = Format(from), To = Format(today) };

            if (!await _repository.IsAvailableAsync())
            {
                dto.Message = "Las estadísticas todavía no están activadas: inicia el servicio python/analytics (crea el esquema analytics y empieza a juntar datos).";
                return dto;
            }
            dto.Available = true;

            var rows = await _repository.GetDailyAsync(channelId, from.AddDays(-days), today);
            var previous = rows.Where(r => r.Day.Date < from).ToList();
            var current = FillDays(rows.Where(r => r.Day.Date >= from).ToList(), previous, from, today);

            var live = current.Where(r => r.MinutesLive > 0).ToList();
            var livePrevious = previous.Where(r => r.MinutesLive > 0).ToList();
            var avgViewers = Mean(live.Select(r => (double)r.AvgViewers));
            var avgPrevious = Mean(livePrevious.Select(r => (double)r.AvgViewers));
            var newFollowers = current.Sum(r => r.NewFollowers);
            var minutesLive = current.Sum(r => r.MinutesLive);
            var messages = current.Sum(r => r.ChatMessages);

            dto.Kpis = new AnalyticsKpisDto
            {
                TotalFollowers = current.Count == 0 ? 0 : current[^1].TotalFollowers,
                NewFollowers = newFollowers,
                FollowersGrowthPct = GrowthPct(newFollowers, previous.Sum(r => r.NewFollowers)),
                AvgViewers = Math.Round(avgViewers, 1),
                AvgViewersGrowthPct = GrowthPct(avgViewers, avgPrevious),
                PeakViewers = current.Count == 0 ? 0 : current.Max(r => r.PeakViewers),
                HoursLive = Math.Round(minutesLive / 60.0, 1),
                HoursWatched = Math.Round(current.Sum(r => r.ViewerMinutes) / 60.0, 1),
                ChatMessages = messages,
                MessagesPerHourLive = minutesLive == 0 ? 0 : Math.Round(messages / (minutesLive / 60.0), 1)
            };

            dto.Daily = current.Select(r => new AnalyticsDayDto
            {
                Day = Format(r.Day),
                NewFollowers = r.NewFollowers,
                TotalFollowers = r.TotalFollowers,
                MinutesLive = r.MinutesLive,
                AvgViewers = (double)r.AvgViewers,
                PeakViewers = r.PeakViewers,
                ChatMessages = r.ChatMessages,
                UniqueChatters = r.UniqueChatters
            }).ToList();

            var nowUtc = _time.GetUtcNow().UtcDateTime;
            dto.Viewers = await _repository.GetViewerSeriesAsync(channelId, nowUtc.AddHours(-ViewerSeriesHours), ViewerBucketMinutes);

            var hours = (await _repository.GetHoursAsync(channelId, from, today)).ToDictionary(h => h.Hour);
            dto.Hours = Enumerable.Range(0, 24)
                .Select(h => hours.TryGetValue(h, out var found) ? found : new AnalyticsHourDto { Hour = h })
                .ToList();

            dto.Trend = await _repository.GetTrendAsync(channelId);

            var etl = await _repository.GetEtlStatusAsync();
            dto.LastUpdatedUtc = etl.LastOkUtc;
            dto.LastError = etl.LastError;
            if (etl.LastOkUtc == null)
                dto.Message = "Todavía no hay datos: deja corriendo python/analytics unos minutos (junta una foto de los espectadores por minuto).";
            else if (nowUtc - etl.LastOkUtc.Value > StaleAfter)
                dto.Message = $"Los datos son de hace {Math.Round((nowUtc - etl.LastOkUtc.Value).TotalMinutes)} minutos: ¿está corriendo python/analytics?";

            return dto;
        }

        // Los días sin fila (el ETL no corrió, o el canal no existía) se muestran en 0,
        // con el total de seguidores del último día conocido.
        private static List<AnalyticsDailyRow> FillDays(List<AnalyticsDailyRow> rows, List<AnalyticsDailyRow> before, DateTime from, DateTime to)
        {
            var byDay = rows.ToDictionary(r => r.Day.Date);
            var lastTotal = before.Count == 0 ? 0 : before.MaxBy(r => r.Day)!.TotalFollowers;
            var result = new List<AnalyticsDailyRow>();
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                if (byDay.TryGetValue(day, out var row))
                {
                    lastTotal = row.TotalFollowers;
                    result.Add(row);
                }
                else
                {
                    result.Add(new AnalyticsDailyRow { Day = day, TotalFollowers = lastTotal });
                }
            }
            return result;
        }

        // null cuando antes no había nada (no se puede dividir por cero), igual que en Python.
        public static double? GrowthPct(double current, double previous) =>
            previous == 0 ? null : Math.Round((current - previous) / previous * 100, 1);

        private static double Mean(IEnumerable<double> values)
        {
            var list = values.ToList();
            return list.Count == 0 ? 0 : list.Average();
        }

        private static string Format(DateTime day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }
}
