using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services;
using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddApplication(
            this IServiceCollection services)
        {
            services.AddMemoryCache();
            services.TryAddSingleton(TimeProvider.System);

            services.AddScoped<IUserService, UserService>();
            services.AddScoped<IChannelService, ChannelService>();
            services.AddScoped<IFollowService, FollowService>();

            // Fase 1: chat en vivo y ciclo de vida de las transmisiones
            services.AddScoped<IChatService, ChatService>();
            services.AddScoped<IStreamLifecycleService, StreamLifecycleService>();

            // Fase 2: estado del chat en memoria (una instancia de la app)
            services.AddSingleton<RecentMessageCache>();
            services.AddSingleton<AutoModQueue>();
            services.AddSingleton<IPollService, PollService>();

            // Fase 4: gráficos del Dashboard (lee analytics.*, que llena python/analytics)
            services.AddScoped<IChannelAnalyticsService, ChannelAnalyticsService>();

            return services;
        }
    }
}
