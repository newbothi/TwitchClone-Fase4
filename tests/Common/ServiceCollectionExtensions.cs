using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common
{
    public static class ServiceCollectionExtensions
    {
        // Registra todos los dobles en memoria en lugar de SQL Server y MongoDB.
        // Se combina con AddApplication() para probar los servicios REALES de la app.
        // Llamar DESPUÉS de AddApplication(): reemplaza el reloj y el IFollowService.
        public static IServiceCollection AddInMemoryPlatform(this IServiceCollection services, InMemoryPlatform platform)
        {
            services.AddSingleton(platform);
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(platform.Clock));

            services.AddScoped<IChannelRepository, InMemoryChannelRepository>();
            services.AddScoped<IUserRepository, InMemoryUserRepository>();
            services.Replace(ServiceDescriptor.Scoped<IFollowService, InMemoryFollowService>());
            services.AddScoped<IModerationService, InMemoryModerationService>();
            services.AddSingleton<IChatMessageService, InMemoryChatMessageService>();
            services.AddSingleton<IChatFilterService, InMemoryChatFilterService>();
            services.AddSingleton<INotificationService, InMemoryNotificationService>();
            services.AddSingleton<ILiveMetricService, InMemoryLiveMetricService>();
            services.AddSingleton<IStreamSessionService, InMemoryStreamSessionService>();

            // Fase 2
            services.AddScoped<IChannelRoleRepository, InMemoryChannelRoleRepository>();
            services.AddScoped<IChatUserRepository, InMemoryChatUserRepository>();
            services.AddSingleton<IChatUserSettingsService, InMemoryChatUserSettingsService>();
            services.AddSingleton<IWhisperService, InMemoryWhisperService>();
            services.AddSingleton(platform.AutoMod);
            services.AddSingleton<IAutoModClassifier>(sp => sp.GetRequiredService<ScriptedAutoModClassifier>());

            // Fase 4
            services.AddSingleton<IAutoModFeedbackSink>(platform.AutoModFeedback);
            return services;
        }
    }
}
