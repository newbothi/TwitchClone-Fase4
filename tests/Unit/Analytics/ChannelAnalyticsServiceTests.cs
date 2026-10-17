using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Unit.Analytics
{
    // Fase 4 (c): lo que reciben los gráficos del Dashboard.
    [TestClass]
    public class ChannelAnalyticsServiceTests
    {
        // 16-oct-2026 20:30 UTC (el reloj de prueba usa UTC como hora "local").
        private static readonly DateTimeOffset Now = new(2026, 10, 16, 20, 30, 0, TimeSpan.Zero);
        private static readonly DateTime Today = new(2026, 10, 16);

        private FakeAnalyticsRepository _repo = null!;
        private ManualTimeProvider _clock = null!;
        private ChannelAnalyticsService _service = null!;

        [TestInitialize]
        public void Setup()
        {
            _repo = new FakeAnalyticsRepository { LastOkUtc = Now.UtcDateTime.AddMinutes(-3) };
            _clock = new ManualTimeProvider(Now);
            _service = new ChannelAnalyticsService(_repo, _clock);
        }

        private static AnalyticsDailyRow Day(int daysAgo, int total, int @new = 0, int minutes = 0, decimal avg = 0, int peak = 0, int messages = 0) =>
            new() { Day = Today.AddDays(-daysAgo), TotalFollowers = total, NewFollowers = @new, MinutesLive = minutes, AvgViewers = avg, PeakViewers = peak, ChatMessages = messages, ViewerMinutes = (long)(avg * minutes) };

        [TestMethod]
        public async Task SinEsquemaAnalytics_AvisaQueHayQueIniciarElServicioPython()
        {
            _repo.Available = false;

            var dto = await _service.GetAsync(1, 30);

            Assert.IsFalse(dto.Available);
            StringAssert.Contains(dto.Message, "python/analytics");
            Assert.AreEqual(0, _repo.DailyQueries, "Ni siquiera intenta leer las tablas.");
        }

        [TestMethod]
        [DataRow(7, "2026-10-10")]
        [DataRow(30, "2026-09-17")]
        [DataRow(90, "2026-07-19")]
        [DataRow(15, "2026-09-17", DisplayName = "Período no permitido → 30 días")]
        public async Task ElPeriodoDefineElRangoDeDias(int days, string expectedFrom)
        {
            var dto = await _service.GetAsync(1, days);

            Assert.AreEqual(expectedFrom, dto.From);
            Assert.AreEqual("2026-10-16", dto.To);
            Assert.AreEqual(dto.Days, dto.Daily.Count, "Un punto por día, aunque no haya datos.");
        }

        [TestMethod]
        public async Task LosDiasSinDatos_SeRellenanConElTotalAnteriorDeSeguidores()
        {
            _repo.Daily = new() { Day(10, 40), Day(5, 45, @new: 5), Day(0, 47, @new: 2) };

            var dto = await _service.GetAsync(1, 7);

            CollectionAssert.AreEqual(new[] { 40, 45, 45, 45, 45, 45, 47 }, dto.Daily.Select(d => d.TotalFollowers).ToList());
            CollectionAssert.AreEqual(new[] { 0, 5, 0, 0, 0, 0, 2 }, dto.Daily.Select(d => d.NewFollowers).ToList());
        }

        [TestMethod]
        public async Task Kpis_ComparanConElPeriodoAnterior()
        {
            // Período anterior (8 a 14 días atrás): 4 nuevos, 1 día en vivo con 10 de promedio.
            // Período actual (últimos 7 días): 6 nuevos, 2 días en vivo con 12 y 18 de promedio.
            _repo.Daily = new()
            {
                Day(12, 100, @new: 4, minutes: 60, avg: 10, peak: 15, messages: 100),
                Day(3, 103, @new: 3, minutes: 120, avg: 12, peak: 20, messages: 300),
                Day(1, 106, @new: 3, minutes: 60, avg: 18, peak: 25, messages: 150),
            };

            var k = (await _service.GetAsync(1, 7)).Kpis;

            Assert.AreEqual(106, k.TotalFollowers);
            Assert.AreEqual(6, k.NewFollowers);
            Assert.AreEqual(50.0, k.FollowersGrowthPct);
            Assert.AreEqual(15.0, k.AvgViewers, "Promedio de los días con stream: (12 + 18) / 2.");
            Assert.AreEqual(50.0, k.AvgViewersGrowthPct);
            Assert.AreEqual(25, k.PeakViewers);
            Assert.AreEqual(3.0, k.HoursLive);
            Assert.AreEqual(450, k.ChatMessages);
            Assert.AreEqual(150.0, k.MessagesPerHourLive);
            Assert.AreEqual(Math.Round((12 * 120 + 18 * 60) / 60.0, 1), k.HoursWatched);
        }

        [TestMethod]
        public async Task SinPeriodoAnterior_ElCrecimientoEsNull()
        {
            _repo.Daily = new() { Day(1, 5, @new: 5, minutes: 30, avg: 3) };

            var k = (await _service.GetAsync(1, 7)).Kpis;

            Assert.IsNull(k.FollowersGrowthPct);
            Assert.IsNull(k.AvgViewersGrowthPct);
        }

        [TestMethod]
        public async Task LasHorasSiempreVanDe0A23()
        {
            _repo.Hours = new() { new AnalyticsHourDto { Hour = 21, AvgViewers = 30, ChatMessages = 50 } };

            var hours = (await _service.GetAsync(1, 30)).Hours;

            CollectionAssert.AreEqual(Enumerable.Range(0, 24).ToList(), hours.Select(h => h.Hour).ToList());
            Assert.AreEqual(30, hours[21].AvgViewers);
            Assert.AreEqual(0, hours[20].AvgViewers);
        }

        [TestMethod]
        public async Task LaHistoriaDeViewersPideLasUltimas24HorasEnTramosDe5Minutos()
        {
            await _service.GetAsync(1, 30);

            Assert.AreEqual(Now.UtcDateTime.AddHours(-24), _repo.LastViewerFrom);
            Assert.AreEqual(5, _repo.LastViewerBucket);
        }

        [TestMethod]
        public async Task SiElEtlNuncaCorrio_LoDice()
        {
            _repo.LastOkUtc = null;

            var dto = await _service.GetAsync(1, 30);

            Assert.IsTrue(dto.Available);
            StringAssert.Contains(dto.Message, "Todavía no hay datos");
        }

        [TestMethod]
        public async Task SiLosDatosEstanViejos_AvisaQueRevisesElServicio()
        {
            _repo.LastOkUtc = Now.UtcDateTime.AddMinutes(-45);

            var dto = await _service.GetAsync(1, 30);

            StringAssert.Contains(dto.Message, "hace 45 minutos");
        }

        [TestMethod]
        public async Task DatosAlDia_SinMensaje()
        {
            var dto = await _service.GetAsync(1, 30);

            Assert.IsNull(dto.Message);
            Assert.AreEqual(_repo.LastOkUtc, dto.LastUpdatedUtc);
        }

        [TestMethod]
        [DataRow(150.0, 100.0, 50.0)]
        [DataRow(50.0, 100.0, -50.0)]
        [DataRow(1.0, 3.0, -66.7)]
        [DataRow(10.0, 0.0, null)]
        public void GrowthPct_IgualQueEnPython(double current, double previous, double? expected)
        {
            Assert.AreEqual(expected, ChannelAnalyticsService.GrowthPct(current, previous));
        }

        private sealed class FakeAnalyticsRepository : IChannelAnalyticsRepository
        {
            public bool Available { get; set; } = true;
            public List<AnalyticsDailyRow> Daily { get; set; } = new();
            public List<AnalyticsHourDto> Hours { get; set; } = new();
            public DateTime? LastOkUtc { get; set; }
            public int DailyQueries { get; private set; }
            public DateTime LastViewerFrom { get; private set; }
            public int LastViewerBucket { get; private set; }

            public Task<bool> IsAvailableAsync() => Task.FromResult(Available);

            public Task<List<AnalyticsDailyRow>> GetDailyAsync(int channelId, DateTime fromDay, DateTime toDay)
            {
                DailyQueries++;
                return Task.FromResult(Daily.Where(d => d.Day >= fromDay && d.Day <= toDay).OrderBy(d => d.Day).ToList());
            }

            public Task<List<AnalyticsViewerPointDto>> GetViewerSeriesAsync(int channelId, DateTime fromUtc, int bucketMinutes)
            {
                LastViewerFrom = fromUtc;
                LastViewerBucket = bucketMinutes;
                return Task.FromResult(new List<AnalyticsViewerPointDto>());
            }

            public Task<List<AnalyticsHourDto>> GetHoursAsync(int channelId, DateTime fromDay, DateTime toDay) => Task.FromResult(Hours);
            public Task<AnalyticsTrendDto?> GetTrendAsync(int channelId) => Task.FromResult<AnalyticsTrendDto?>(null);
            public Task<AnalyticsEtlStatus> GetEtlStatusAsync() => Task.FromResult(new AnalyticsEtlStatus(LastOkUtc, null));
        }
    }
}
