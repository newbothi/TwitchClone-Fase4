using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;

using Microsoft.AspNetCore.Identity;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    public interface IUserService
    {
        Task<IdentityResult> RegisterAsync(RegisterDto dto);
        Task<bool> LoginAsync(LoginDto dto);
        Task LogoutAsync();
        Task<UserProfileDto?> GetProfileAsync(string email, string? currentUserId);
        Task<bool> ResetPasswordAsync(ResetPasswordDto dto);
    }
}
