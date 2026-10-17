using Microsoft.AspNetCore.Identity;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services
{
    public class UserService : IUserService
    {
        private readonly UserManager<AppUser> _userManager;
        private readonly SignInManager<AppUser> _signInManager;
        private readonly IUserRepository _userRepository;
        private readonly IFollowRepository _followRepository;
        private readonly IChannelRepository _channelRepository;

        public UserService(
            UserManager<AppUser> userManager,
            SignInManager<AppUser> signInManager,
            IUserRepository userRepository,
            IFollowRepository followRepository,
            IChannelRepository channelRepository)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _userRepository = userRepository;
            _followRepository = followRepository;
            _channelRepository = channelRepository;
        }

        public async Task<IdentityResult> RegisterAsync(RegisterDto dto)
        {
            var user = new AppUser
            {
                UserName = dto.Email,
                Email = dto.Email,
                DisplayUsername = dto.DisplayUsername,
                CreatedAt = DateTime.Now
            };

            var result = await _userManager.CreateAsync(user, dto.Password);

            if (result.Succeeded)
            {
                // Asignar rol
                await _userManager.AddToRoleAsync(user, dto.Role);
            }

            return result;
        }

        public async Task<bool> LoginAsync(LoginDto dto)
        {
            var result = await _signInManager.PasswordSignInAsync(
                dto.Email,
                dto.Password,
                dto.RememberMe,
                lockoutOnFailure: false);

            return result.Succeeded;
        }

        public async Task LogoutAsync()
        {
            await _signInManager.SignOutAsync();
        }

        public async Task<UserProfileDto?> GetProfileAsync(string email, string? currentUserId)
        {
            var user = await _userRepository.GetByEmailAsync(email);
            if (user == null) return null;

            var channel = await _channelRepository.GetByUserIdAsync(user.Id);
            var followerCount = await _followRepository.GetFollowerCountAsync(user.Id);
            var followingCount = await _followRepository.GetFollowingCountAsync(user.Id);

            bool isFollowed = false;
            if (currentUserId != null && currentUserId != user.Id)
                isFollowed = await _followRepository.IsFollowingAsync(currentUserId, user.Id);

            return new UserProfileDto
            {
                Id = user.Id,
                DisplayUsername = user.DisplayUsername,
                Email = user.Email ?? string.Empty,
                Points = user.Points,
                IsLive = channel?.IsLive ?? false,
                FollowerCount = followerCount,
                FollowingCount = followingCount,
                IsFollowedByCurrentUser = isFollowed,
                Channel = channel == null ? null : new ChannelDto
                {
                    Id = channel.Id,
                    UserId = channel.UserId,
                    DisplayUsername = user.DisplayUsername,
                    Name = channel.Name,
                    Description = channel.Description,
                    IsLive = channel.IsLive,
                    ViewerCount = channel.ViewerCount
                }
            };
        }

        public async Task<bool> ResetPasswordAsync(ResetPasswordDto dto)
        {
            var user = await _userManager.FindByIdAsync(dto.UserId);
            if (user == null) return false;

            var result = await _userManager.ResetPasswordAsync(
                user,
                dto.Token,
                dto.NewPassword);

            return result.Succeeded;
        }
    }
}