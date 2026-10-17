using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    public interface IFollowRepository
    {
        Task<bool> IsFollowingAsync(string followerId, string followedId);
        Task<int> GetFollowerCountAsync(string userId);
        Task<int> GetFollowingCountAsync(string userId);
        Task<IEnumerable<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.AppUser>> GetFollowersAsync(string userId);
        Task<IEnumerable<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.AppUser>> GetFollowingAsync(string userId);
        Task FollowAsync(string followerId, string followedId);
        Task UnfollowAsync(string followerId, string followedId);
    }
}
