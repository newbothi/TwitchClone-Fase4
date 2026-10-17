using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services
{
    public class StreamLifecycleService : IStreamLifecycleService
    {
        private readonly IChannelRepository _channels;
        private readonly IFollowService _follows;
        private readonly INotificationService _notifications;
        private readonly ILiveMetricService _metrics;
        private readonly IStreamSessionService _sessions;
        private readonly ILogger<StreamLifecycleService> _logger;

        public StreamLifecycleService(
            IChannelRepository channels,
            IFollowService follows,
            INotificationService notifications,
            ILiveMetricService metrics,
            IStreamSessionService sessions,
            ILogger<StreamLifecycleService> logger)
        {
            _channels = channels;
            _follows = follows;
            _notifications = notifications;
            _metrics = metrics;
            _sessions = sessions;
            _logger = logger;
        }

        public async Task<PublishAuthorization> AuthorizePublishAsync(int channelId, string? providedKey)
        {
            var info = await _channels.GetStreamInfoAsync(channelId);
            if (info == null) return PublishAuthorization.ChannelNotFound;
            if (string.IsNullOrEmpty(providedKey)) return PublishAuthorization.MissingKey;

            // Comparación en tiempo constante: no filtra cuántos caracteres coinciden.
            var expected = Encoding.UTF8.GetBytes(info.StreamKey ?? string.Empty);
            var provided = Encoding.UTF8.GetBytes(providedKey);
            if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(expected, provided))
                return PublishAuthorization.InvalidKey;

            if (info.OwnerIsSuspended) return PublishAuthorization.OwnerSuspended;

            return PublishAuthorization.Allowed;
        }

        public async Task<StreamStateChange?> MarkLiveAsync(int channelId)
        {
            var info = await _channels.GetStreamInfoAsync(channelId);
            if (info == null) return null;

            var changed = await _channels.SetLiveStateAsync(channelId, true);
            var result = new StreamStateChange(channelId, changed, info.Name, info.OwnerDisplayName);
            if (!changed) return result; // webhook repetido o reconciliación: nada más que hacer

            // MongoDB: métricas y sesión. Si Mongo está caído, el canal igual queda EN VIVO.
            try
            {
                await _metrics.StartStreamAsync(channelId.ToString());
                await _sessions.StartSessionAsync(channelId.ToString(), info.Name, info.Name, info.CategoryName ?? "Sin categoría");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo iniciar la sesión/métricas del canal {ChannelId} en MongoDB.", channelId);
            }

            try
            {
                var followerIds = (await _follows.GetFollowersAsync(info.UserId)).Select(u => u.Id).ToList();
                await _notifications.NotifyFollowersAsync(followerIds, channelId.ToString(), info.OwnerDisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudieron crear las notificaciones de 'en vivo' del canal {ChannelId}.", channelId);
            }

            _logger.LogInformation("Canal {ChannelId} ({ChannelName}) está EN VIVO.", channelId, info.Name);
            return result;
        }

        public async Task<StreamStateChange?> MarkOfflineAsync(int channelId)
        {
            var info = await _channels.GetStreamInfoAsync(channelId);
            if (info == null) return null;

            var changed = await _channels.SetLiveStateAsync(channelId, false);
            var result = new StreamStateChange(channelId, changed, info.Name, info.OwnerDisplayName);
            if (!changed) return result;

            try
            {
                var key = channelId.ToString();
                var peak = (await _metrics.GetMetricAsync(key))?.PeakViewers ?? 0;
                await _metrics.StopStreamAsync(key);
                await _sessions.EndSessionAsync(key, peak, 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo cerrar la sesión/métricas del canal {ChannelId} en MongoDB.", channelId);
            }

            _logger.LogInformation("Canal {ChannelId} ({ChannelName}) quedó OFFLINE.", channelId, info.Name);
            return result;
        }
    }
}
