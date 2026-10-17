using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    public class UserController : Controller
    {
        // Límite de enlaces sociales por perfil (ver captura "Enlaces sociales" de Twitch: hasta 5)
        private const int MaxSocialLinks = 5;
        private const long MaxImageBytes = 10 * 1024 * 1024; // 10 MB, igual que el límite que muestra Twitch
        private static readonly string[] AllowedImageExtensions = { ".jpg", ".jpeg", ".png", ".gif" };

        private readonly UserManager<AppUser> _userManager;
        private readonly IFollowService _followService;
        private readonly IChannelService _channelService;
        private readonly INotificationService _notificationService;
        private readonly IHubContext<StreamHub> _hubContext;
        private readonly AppDbContext _context;

        public UserController(
            UserManager<AppUser> userManager,
            IFollowService followService,
            IChannelService channelService,
            INotificationService notificationService,
            IHubContext<StreamHub> hubContext,
            AppDbContext context)
        {
            _userManager = userManager;
            _followService = followService;
            _channelService = channelService;
            _notificationService = notificationService;
            _hubContext = hubContext;
            _context = context;
        }

        // GET: /User/Profile/username
        [HttpGet("User/Profile/{username}")]
        public async Task<IActionResult> Profile(string username)
        {
            var profileUser = await _userManager.FindByNameAsync(username);
            if (profileUser == null) return NotFound();

            // Obtener canal del usuario
            var channel = await _channelService.GetByUserIdAsync(profileUser.Id);
            var followerCount = await _followService.GetFollowerCountAsync(profileUser.Id);
            var followingCount = await _followService.GetFollowingCountAsync(profileUser.Id);
            var followers = await _followService.GetFollowersAsync(profileUser.Id);
            var following = await _followService.GetFollowingAsync(profileUser.Id);
            var socialLinks = await _context.SocialLinks
                .Where(s => s.UserId == profileUser.Id)
                .OrderBy(s => s.SortOrder)
                .ToListAsync();

            bool isFollowing = false;
            bool isOwnProfile = false;

            if (User.Identity!.IsAuthenticated)
            {
                var currentUser = await _userManager.GetUserAsync(User);
                if (currentUser != null)
                {
                    isFollowing = await _followService.IsFollowingAsync(currentUser.Id, profileUser.Id);
                    isOwnProfile = currentUser.Id == profileUser.Id;
                }
            }

            ViewBag.Channel = channel;
            ViewBag.IsFollowing = isFollowing;
            ViewBag.IsOwnProfile = isOwnProfile;
            ViewBag.Followers = followers;
            ViewBag.Following = following;

            return View(new UserProfileDto
            {
                Id = profileUser.Id,
                DisplayUsername = profileUser.DisplayUsername,
                Email = profileUser.Email!,
                Points = profileUser.Points,
                IsLive = channel?.IsLive ?? false,
                FollowerCount = followerCount,
                FollowingCount = followingCount,
                IsFollowedByCurrentUser = isFollowing,
                Channel = channel,
                AvatarUrl = profileUser.AvatarUrl,
                BannerUrl = profileUser.BannerUrl,
                Bio = profileUser.Bio,
                SocialLinks = socialLinks.Select(s => new SocialLinkDto { Id = s.Id, Title = s.Title, Url = s.Url }).ToList()
            });
        }

        // GET: /User/EditProfile
        [Authorize]
        public async Task<IActionResult> EditProfile()
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return RedirectToAction("Login", "Account");

            var socialLinks = await _context.SocialLinks
                .Where(s => s.UserId == currentUser.Id)
                .OrderBy(s => s.SortOrder)
                .ToListAsync();

            ViewBag.User = currentUser;
            ViewBag.SocialLinks = socialLinks;

            return View(new EditProfileDto
            {
                DisplayUsername = currentUser.DisplayUsername,
                Bio = currentUser.Bio
            });
        }

        // POST: /User/EditProfile
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditProfile(EditProfileDto model)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return RedirectToAction("Login", "Account");

            if (!ModelState.IsValid)
            {
                ViewBag.User = currentUser;
                ViewBag.SocialLinks = await _context.SocialLinks
                    .Where(s => s.UserId == currentUser.Id)
                    .OrderBy(s => s.SortOrder)
                    .ToListAsync();
                return View(model);
            }

            currentUser.DisplayUsername = model.DisplayUsername;
            currentUser.Bio = model.Bio;
            await _userManager.UpdateAsync(currentUser);

            TempData["Success"] = "Perfil actualizado correctamente";
            return RedirectToAction("EditProfile");
        }

        // POST: /User/UploadAvatar
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadAvatar(IFormFile image)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return RedirectToAction("Login", "Account");

            var validationError = ValidateImage(image);
            if (validationError != null)
            {
                TempData["ProfileImageError"] = validationError;
                return RedirectToAction("EditProfile");
            }

            var relativeUrl = await SaveProfileImageAsync(image!, "avatars", currentUser.Id);
            currentUser.AvatarUrl = relativeUrl;
            await _userManager.UpdateAsync(currentUser);

            TempData["Success"] = "Foto de perfil actualizada";
            return RedirectToAction("EditProfile");
        }

        // POST: /User/UploadBanner
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadBanner(IFormFile image)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return RedirectToAction("Login", "Account");

            var validationError = ValidateImage(image);
            if (validationError != null)
            {
                TempData["ProfileImageError"] = validationError;
                return RedirectToAction("EditProfile");
            }

            var relativeUrl = await SaveProfileImageAsync(image!, "banners", currentUser.Id);
            currentUser.BannerUrl = relativeUrl;
            await _userManager.UpdateAsync(currentUser);

            TempData["Success"] = "Banner actualizado";
            return RedirectToAction("EditProfile");
        }

        // POST: /User/AddSocialLink
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddSocialLink(string title, string url)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return RedirectToAction("Login", "Account");

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
            {
                TempData["SocialLinkError"] = "El título y la URL son obligatorios";
                return RedirectToAction("EditProfile");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) ||
                (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
            {
                TempData["SocialLinkError"] = "La URL debe ser una dirección completa (https://...)";
                return RedirectToAction("EditProfile");
            }

            var currentCount = await _context.SocialLinks.CountAsync(s => s.UserId == currentUser.Id);
            if (currentCount >= MaxSocialLinks)
            {
                TempData["SocialLinkError"] = $"Solo puedes tener hasta {MaxSocialLinks} enlaces sociales";
                return RedirectToAction("EditProfile");
            }

            _context.SocialLinks.Add(new SocialLink
            {
                UserId = currentUser.Id,
                Title = title.Trim(),
                Url = url.Trim(),
                SortOrder = currentCount
            });
            await _context.SaveChangesAsync();

            return RedirectToAction("EditProfile");
        }

        // POST: /User/DeleteSocialLink
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSocialLink(int id)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return RedirectToAction("Login", "Account");

            var link = await _context.SocialLinks.FirstOrDefaultAsync(s => s.Id == id && s.UserId == currentUser.Id);
            if (link != null)
            {
                _context.SocialLinks.Remove(link);
                await _context.SaveChangesAsync();
            }

            return RedirectToAction("EditProfile");
        }

        // POST: /User/Follow
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Follow(string followedId, string returnUrl)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return RedirectToAction("Login", "Account");

            await _followService.FollowAsync(currentUser.Id, followedId);

            // Notificación en MongoDB
            await _notificationService.CreateNotificationAsync(
                userId: followedId,
                message: $"{currentUser.DisplayUsername} te empezó a seguir",
                type: "NewFollower",
                channelName: null,
                channelId: null);

            // Notificar en tiempo real via SignalR
            await _hubContext.Clients.User(followedId)
                .SendAsync("NewFollower", currentUser.DisplayUsername);

            return Redirect(returnUrl ?? "/");
        }

        // POST: /User/Unfollow
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Unfollow(string followedId, string returnUrl)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return RedirectToAction("Login", "Account");

            await _followService.UnfollowAsync(currentUser.Id, followedId);

            return Redirect(returnUrl ?? "/");
        }

        // GET: /User/Following
        [Authorize]
        public async Task<IActionResult> Following()
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return RedirectToAction("Login", "Account");

            var liveChannels = await _followService.GetFollowedLiveChannelsAsync(currentUser.Id);
            return View(liveChannels.ToList());
        }

        // ── Helpers privados ──────────────────────────────────────────

        private static string? ValidateImage(IFormFile? image)
        {
            if (image == null || image.Length == 0)
                return "Debes seleccionar una imagen";

            if (image.Length > MaxImageBytes)
                return "La imagen no puede superar los 10 MB";

            var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            if (!AllowedImageExtensions.Contains(extension))
                return "Formato no soportado. Usa JPEG, PNG o GIF";

            return null;
        }

        private static async Task<string> SaveProfileImageAsync(IFormFile image, string folder, string userId)
        {
            var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", folder);
            Directory.CreateDirectory(uploadsDir);

            // Nombre único por usuario + timestamp, para que el navegador no cachee la imagen vieja
            // al reemplazarla (mismo nombre de archivo = misma URL = caché del navegador no se entera).
            var extension = Path.GetExtension(image.FileName);
            var fileName = $"{userId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{extension}";
            var filePath = Path.Combine(uploadsDir, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
                await image.CopyToAsync(stream);

            return $"/{folder}/{fileName}";
        }
    }
}