using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    public interface INotificationService
    {
        Task CreateNotificationAsync(string userId, string message, string type, string? channelName, string? channelId);
        Task NotifyFollowersAsync(List<string> followerIds, string channelId, string channelName);
        Task<List<Notification>> GetUserNotificationsAsync(string userId);
        Task<List<Notification>> GetUnreadAsync(string userId);
        Task MarkAsReadAsync(string notificationId);
        Task MarkAllAsReadAsync(string userId);
        Task<long> GetUnreadCountAsync(string userId);
        Task DeleteNotificationAsync(string notificationId);
    }
}
