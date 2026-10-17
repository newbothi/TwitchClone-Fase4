using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/follow")]
    public class FollowController : ControllerBase
    {
        private readonly UserManager<AppUser> _userManager;
        private readonly IFollowService _followService;
        private readonly IFollowRepository _followRepository;
        private readonly INotificationService _notificationService;

        public FollowController(
            UserManager<AppUser> userManager,
            IFollowService followService,
            IFollowRepository followRepository,
            INotificationService notificationService)
        {
            _userManager = userManager;
            _followService = followService;
            _followRepository = followRepository;
            _notificationService = notificationService;
        }

        // POST: /api/follow/toggle/{userId}
        [HttpPost("toggle/{userId}")]
        public async Task<IActionResult> ToggleFollow(string userId)
        {
            var follower = await _userManager.GetUserAsync(User);
            if (follower == null) return Unauthorized();

            var followed = await _userManager.FindByIdAsync(userId);
            if (followed == null) return NotFound();

            if (follower.Id == followed.Id)
                return BadRequest(new { success = false, message = "No puedes seguirte a ti mismo" });

            var isFollowing = await _followService.IsFollowingAsync(follower.Id, userId);

            if (isFollowing)
            {
                await _followService.UnfollowAsync(follower.Id, userId);
                return Ok(new { success = true, followed = false, message = "Dejaste de seguir" });
            }
            else
            {
                await _followService.FollowAsync(follower.Id, userId);

                // Crear notificación en MongoDB
                await _notificationService.CreateNotificationAsync(
                    userId: userId,
                    message: $"{follower.DisplayUsername} te empezó a seguir",
                    type: "NewFollower",
                    channelName: null,
                    channelId: null);

                return Ok(new { success = true, followed = true, message = "¡Ahora sigues este usuario!" });
            }
        }

        // GET: /api/follow/status/{userId}
        [HttpGet("status/{userId}")]
        public async Task<IActionResult> GetFollowStatus(string userId)
        {
            var follower = await _userManager.GetUserAsync(User);
            if (follower == null) return Unauthorized();

            var isFollowing = await _followService.IsFollowingAsync(follower.Id, userId);
            return Ok(new { followed = isFollowing });
        }

        // GET: /api/follow/myFollows
        [HttpGet("myFollows")]
        public async Task<IActionResult> GetMyFollows()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            var follows = await _followRepository.GetFollowingAsync(user.Id);

            return Ok(follows.Select(f => new
            {
                f.Id,
                f.DisplayUsername,
                f.Email
            }));
        }

        // GET: /api/follow/count/{userId}
        [HttpGet("count/{userId}")]
        public async Task<IActionResult> GetFollowCount(string userId)
        {
            var count = await _followRepository.GetFollowerCountAsync(userId);
            return Ok(new { count });
        }

        // GET: /api/follow/followers/{userId}
        [HttpGet("followers/{userId}")]
        public async Task<IActionResult> GetFollowers(string userId)
        {
            var followers = await _followRepository.GetFollowersAsync(userId);

            return Ok(followers.Select(f => new
            {
                f.Id,
                f.DisplayUsername,
                f.Email
            }));
        }
    }
}