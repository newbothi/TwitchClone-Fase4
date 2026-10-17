using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Ai;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MediaServer;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Repositories;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructure(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            // DbContext
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlServer(
                    configuration.GetConnectionString("DefaultConnection")));

            // Fábrica de conexiones para Dapper
            services.AddSingleton<DbConnectionFactory>();

            // Identity
            services.AddIdentity<AppUser, IdentityRole>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequiredLength = 6;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

            // Repositories SQL
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IChannelRepository, ChannelRepository>();
            services.AddScoped<IFollowRepository, FollowRepository>();

            // MongoDB
            services.Configure<MongoSettings>(
                configuration.GetSection("MongoSettings"));

            services.AddSingleton<IMongoClient>(sp =>
            {
                var mongoSettings = MongoClientSettings.FromConnectionString(configuration["MongoSettings:ConnectionString"]);
                // Si Mongo no responde, fallar rápido (5s) en vez del default de 30s
                mongoSettings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
                return new MongoClient(mongoSettings);
            });

            // Servicios MongoDB
            services.AddSingleton<IChatMessageService, ChatMessageService>();
            services.AddSingleton<INotificationService, NotificationService>();
            services.AddSingleton<ILiveMetricService, LiveMetricService>();
            services.AddSingleton<IStreamSessionService, StreamSessionService>();
            services.AddSingleton<IChatFilterService, ChatFilterService>();
            services.AddSingleton<IAlertEventService, AlertEventService>();
            services.AddSingleton<IWhisperService, WhisperService>();
            services.AddHostedService<MongoIndexInitializer>();

            // Servicios sobre SQL Server que los controladores pedían pero nunca se
            // registraron (por eso /Clip/... daba error 500).
            services.AddScoped<IModerationService, ModerationService>();
            services.AddScoped<ClipService>();

            // Servidor de medios (MediaMTX)
            services.Configure<MediaServerSettings>(configuration.GetSection(MediaServerSettings.SectionName));
            services.AddHttpClient<IMediaServerClient, MediaMtxClient>((sp, client) =>
            {
                var apiBase = configuration[$"{MediaServerSettings.SectionName}:ApiBaseUrl"] ?? new MediaServerSettings().ApiBaseUrl;
                client.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/");
            });

            // ── Fase 2: chat ──────────────────────────────────────────────────
            services.AddHostedService<ChatMessageWriterService>();   // escritura por lotes en Mongo
            services.AddHostedService<SqlSchemaInitializer>();       // crea la tabla ChannelRoles si no existe
            services.AddScoped<IChannelRoleRepository, ChannelRoleRepository>();
            services.AddScoped<IChatUserRepository, ChatUserRepository>();
            services.AddScoped<IChannelAnalyticsRepository, ChannelAnalyticsRepository>(); // Fase 4
            services.AddSingleton<IChatUserSettingsService, ChatUserSettingsService>();

            // AutoMod con IA: "None" (solo reglas), "Ollama" o "Http" (tu microservicio)
            services.Configure<AutoModSettings>(configuration.GetSection(AutoModSettings.SectionName));
            services.Configure<OllamaSettings>(configuration.GetSection(OllamaSettings.SectionName));
            var ollamaUrl = (configuration[$"{OllamaSettings.SectionName}:Url"] ?? new OllamaSettings().Url).TrimEnd('/') + "/";
            services.AddHttpClient<OllamaAutoModClassifier>(c => c.BaseAddress = new Uri(ollamaUrl));
            services.AddHttpClient<HttpAutoModClassifier>();
            services.AddScoped<IAutoModClassifier>(sp =>
                (configuration[$"{AutoModSettings.SectionName}:Provider"] ?? "None").Trim().ToLowerInvariant() switch
                {
                    "ollama" => sp.GetRequiredService<OllamaAutoModClassifier>(),
                    "http" => sp.GetRequiredService<HttpAutoModClassifier>(),
                    _ => new NullAutoModClassifier()
                });

            // Fase 4: con el AutoMod de Python, las decisiones de los moderadores lo entrenan.
            // Un solo HttpAutoModFeedbackSink (singleton) hace de cola y de servicio en segundo plano.
            var autoModProvider = (configuration[$"{AutoModSettings.SectionName}:Provider"] ?? "None").Trim().ToLowerInvariant();
            if (autoModProvider == "http" && configuration.GetValue($"{AutoModSettings.SectionName}:SendFeedback", true))
            {
                services.AddHttpClient(nameof(HttpAutoModFeedbackSink), c => c.Timeout = TimeSpan.FromSeconds(5));
                services.AddSingleton(sp => new HttpAutoModFeedbackSink(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HttpAutoModFeedbackSink)),
                    sp.GetRequiredService<IOptions<AutoModSettings>>(),
                    sp.GetRequiredService<ILogger<HttpAutoModFeedbackSink>>()));
                services.AddSingleton<IAutoModFeedbackSink>(sp => sp.GetRequiredService<HttpAutoModFeedbackSink>());
                services.AddHostedService(sp => sp.GetRequiredService<HttpAutoModFeedbackSink>());
            }
            else
            {
                services.AddSingleton<IAutoModFeedbackSink, NullAutoModFeedbackSink>();
            }

            // ── Fase 3: simulación ────────────────────────────────────────────
            services.Configure<SimulationSettings>(configuration.GetSection(SimulationSettings.SectionName));
            services.Configure<PointsSettings>(configuration.GetSection(PointsSettings.SectionName));
            services.AddHttpClient<IAiChatGenerator, OllamaChatGenerator>(c =>
            {
                c.BaseAddress = new Uri(ollamaUrl);
                c.Timeout = TimeSpan.FromSeconds(60);
            });

            return services;
        }
    }
}