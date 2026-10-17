using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    // Moderadores y VIP por canal (tabla ChannelRoles en SQL Server, vía Dapper).
    public interface IChannelRoleRepository
    {
        // userId → rol, para todos los usuarios con rol en el canal.
        Task<Dictionary<string, ChannelRoleType>> GetRolesAsync(int channelId);

        Task<List<ChannelRoleEntry>> ListAsync(int channelId);

        // ChannelRoleType.None elimina el rol.
        Task SetRoleAsync(int channelId, string userId, ChannelRoleType role, string grantedById);
    }
}
