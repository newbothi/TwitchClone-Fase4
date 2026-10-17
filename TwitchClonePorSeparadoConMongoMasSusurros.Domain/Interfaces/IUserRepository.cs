using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    public interface IUserRepository
    {
        Task<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.AppUser?> GetByIdAsync(string userId);
        Task<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.AppUser?> GetByEmailAsync(string email);
        Task<IEnumerable<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.AppUser>> SearchUsersAsync(string query);
        Task UpdateAsync(TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.AppUser user);
    }
}
