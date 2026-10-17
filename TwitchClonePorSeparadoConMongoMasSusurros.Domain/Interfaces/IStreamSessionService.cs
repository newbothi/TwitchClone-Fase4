using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    public interface IStreamSessionService
    {
        Task<StreamSession> StartSessionAsync(string channelId, string channelName, string title, string categoryName);
        Task EndSessionAsync(string channelId, int peakViewers, int avgViewers);
        Task<List<StreamSession>> GetChannelHistoryAsync(string channelId, int limit = 20);
        Task<StreamSession?> GetActiveSessionAsync(string channelId);
    }
}


