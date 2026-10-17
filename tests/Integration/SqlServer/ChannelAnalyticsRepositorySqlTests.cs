using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Repositories;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Integration.SqlServer
{
    // Fase 4 (c): las consultas Dapper del Dashboard contra un SQL Server DE VERDAD.
    // Crea una base temporal, corre sql/fase4_analytics.sql, inserta datos y la borra al final.
    // No toca la base de la app.
    //
    // Para correrlas (PowerShell, en la carpeta de la solución):
    //   $env:PRUEBAS_SQLSERVER = "Server=localhost;Trusted_Connection=True;TrustServerCertificate=True;"
    //   dotnet test tests\Integration --filter TestCategory=SqlServer
    // Sin esa variable se marcan como omitidas (Inconclusive).
    [TestClass]
    [TestCategory("SqlServer")]
    public class ChannelAnalyticsRepositorySqlTests
    {
        private static string? _database;
        private static string? _connectionString;

        private static string? Server => Environment.GetEnvironmentVariable("PRUEBAS_SQLSERVER");

        private static string Master => new SqlConnectionStringBuilder(Server) { InitialCatalog = "master" }.ConnectionString;

        private ChannelAnalyticsRepository Repository()
        {
            if (_connectionString == null) Assert.Inconclusive("Define PRUEBAS_SQLSERVER (cadena de conexión a tu SQL Server) para correr estas pruebas.");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _connectionString })
                .Build();
            return new ChannelAnalyticsRepository(new DbConnectionFactory(configuration));
        }

        [TestInitialize]
        public async Task CreateDatabaseOnce()
        {
            if (string.IsNullOrWhiteSpace(Server) || _connectionString != null) return;

            _database = $"TwitchClonePruebas_{Guid.NewGuid():N}"[..34];
            await using (var master = new SqlConnection(Master))
                await master.ExecuteAsync($"CREATE DATABASE [{_database}]");

            _connectionString = new SqlConnectionStringBuilder(Server) { InitialCatalog = _database }.ConnectionString;
            await using var db = new SqlConnection(_connectionString);
            foreach (var batch in Regex.Split(await File.ReadAllTextAsync(FindSchemaScript()), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(batch)) await db.ExecuteAsync(batch);
            }

            // Datos de ejemplo del canal 7, como los dejaría python/analytics.
            await db.ExecuteAsync("""
                INSERT INTO analytics.ChannelDaily (ChannelId, [Day], NewFollowers, TotalFollowers, MinutesLive, AvgViewers, PeakViewers,
                    ViewerMinutes, ChatMessages, UniqueChatters, SimulatedMessages, UpdatedAtUtc)
                VALUES (7, '2026-10-14', 2, 40, 0,   0,     0,  0,    0,   0,  0, SYSUTCDATETIME()),
                       (7, '2026-10-15', 3, 43, 90,  12.50, 20, 1125, 300, 25, 10, SYSUTCDATETIME()),
                       (7, '2026-10-16', 1, 44, 60,  18.00, 30, 1080, 120, 12, 0, SYSUTCDATETIME()),
                       (8, '2026-10-16', 9, 99, 60,  50.00, 60, 3000, 999, 50, 0, SYSUTCDATETIME());
                INSERT INTO analytics.ChannelHourly (ChannelId, [Day], [Hour], AvgViewers, PeakViewers, Samples, ChatMessages, UpdatedAtUtc)
                VALUES (7, '2026-10-15', 21, 10, 15, 60, 100, SYSUTCDATETIME()),
                       (7, '2026-10-16', 21, 20, 30, 60, 50,  SYSUTCDATETIME()),
                       (7, '2026-10-16', 3,  0,  0,  0,  7,   SYSUTCDATETIME());
                INSERT INTO analytics.ViewerSnapshots (ChannelId, CapturedAtUtc, Viewers, CategoryId)
                VALUES (7, '2026-10-16 20:00:00', 10, 5), (7, '2026-10-16 20:01:00', 20, 5), (7, '2026-10-16 20:06:00', 30, 5),
                       (7, '2026-10-15 10:00:00', 99, 5), (8, '2026-10-16 20:00:00', 77, 5);
                INSERT INTO analytics.ChannelTrends (ChannelId, WindowDays, FollowersPerDay, FollowersR2, ProjectedFollowers7d,
                    FollowersGrowthPct, AvgViewersPerDay, ViewersGrowthPct, UpdatedAtUtc)
                VALUES (7, 14, 1.25, 0.9, 53, NULL, 0.5, 12.5, '2026-10-16 20:10:00');
                INSERT INTO analytics.EtlRuns (Job, StartedAtUtc, FinishedAtUtc, RowsWritten, Ok, Error)
                VALUES (N'aggregate', '2026-10-16 20:00:00', '2026-10-16 20:00:05', 10, 1, NULL),
                       (N'snapshot',  '2026-10-16 20:01:00', '2026-10-16 20:01:01', 0,  0, N'MongoDB no disponible: prueba');
                """);
        }

        [ClassCleanup]
        public static async Task DropDatabase()
        {
            if (_database == null) return;
            SqlConnection.ClearAllPools();
            await using var master = new SqlConnection(Master);
            await master.ExecuteAsync($"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}];");
        }

        private static string FindSchemaScript()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "sql", "fase4_analytics.sql");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("No encontré sql/fase4_analytics.sql subiendo desde la carpeta de las pruebas.");
        }

        [TestMethod]
        public async Task ElEsquemaExiste()
        {
            Assert.IsTrue(await Repository().IsAvailableAsync());
        }

        [TestMethod]
        public async Task LeeLosDiasDelCanalEnElRango()
        {
            var rows = await Repository().GetDailyAsync(7, new DateTime(2026, 10, 15), new DateTime(2026, 10, 16));

            Assert.AreEqual(2, rows.Count, "Solo el canal 7 y solo el rango pedido.");
            Assert.AreEqual(new DateTime(2026, 10, 15), rows[0].Day);
            Assert.AreEqual(12.50m, rows[0].AvgViewers);
            Assert.AreEqual(1125L, rows[0].ViewerMinutes);
            Assert.AreEqual(44, rows[1].TotalFollowers);
        }

        [TestMethod]
        public async Task AgrupaLaHistoriaDeViewersEnTramos()
        {
            var points = await Repository().GetViewerSeriesAsync(7, new DateTime(2026, 10, 16, 19, 0, 0, DateTimeKind.Utc), 5);

            Assert.AreEqual(2, points.Count, "20:00–20:04 y 20:05–20:09.");
            Assert.AreEqual(new DateTime(2026, 10, 16, 20, 0, 0), points[0].TimeUtc);
            Assert.AreEqual(DateTimeKind.Utc, points[0].TimeUtc.Kind);
            Assert.AreEqual(15.0, points[0].AvgViewers);
            Assert.AreEqual(20, points[0].PeakViewers);
            Assert.AreEqual(30, points[1].PeakViewers);
        }

        [TestMethod]
        public async Task PromedioPorHora_IgnoraLasHorasSinFotos()
        {
            var hours = await Repository().GetHoursAsync(7, new DateTime(2026, 10, 15), new DateTime(2026, 10, 16));

            var at21 = hours.Single(h => h.Hour == 21);
            Assert.AreEqual(15.0, at21.AvgViewers);
            Assert.AreEqual(150, at21.ChatMessages);
            var at3 = hours.Single(h => h.Hour == 3);
            Assert.AreEqual(0.0, at3.AvgViewers, "Solo hubo chat, sin fotos de viewers.");
            Assert.AreEqual(7, at3.ChatMessages);
        }

        [TestMethod]
        public async Task LeeLaTendenciaConNulos()
        {
            var trend = await Repository().GetTrendAsync(7);

            Assert.IsNotNull(trend);
            Assert.AreEqual(1.25, trend.FollowersPerDay);
            Assert.AreEqual(53, trend.ProjectedFollowers7d);
            Assert.IsNull(trend.FollowersGrowthPct);
            Assert.AreEqual(12.5, trend.ViewersGrowthPct);
            Assert.AreEqual(DateTimeKind.Utc, trend.UpdatedAtUtc.Kind);
            Assert.IsNull(await Repository().GetTrendAsync(12345));
        }

        [TestMethod]
        public async Task EstadoDelEtl_UltimoOkYUltimoError()
        {
            var status = await Repository().GetEtlStatusAsync();

            Assert.AreEqual(new DateTime(2026, 10, 16, 20, 0, 5), status.LastOkUtc);
            Assert.AreEqual("MongoDB no disponible: prueba", status.LastError);
        }
    }
}
