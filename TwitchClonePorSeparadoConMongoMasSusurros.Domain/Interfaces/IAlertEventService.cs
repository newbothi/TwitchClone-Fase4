using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    public interface IAlertEventService
    {
        Task CreateAlertAsync(string channelId, string type, string fromUserName, string message, int amount = 0);
        Task<List<AlertEvent>> GetPendingAlertsAsync(string channelId);
        Task MarkAsShownAsync(string alertId);
        Task<List<AlertEvent>> GetAlertHistoryAsync(string channelId, int limit = 50);
    }
}
