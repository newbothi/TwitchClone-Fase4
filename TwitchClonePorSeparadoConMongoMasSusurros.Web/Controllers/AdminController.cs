using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Simulation;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    [Authorize(Roles = "Admin")]
    [AutoValidateAntiforgeryToken]
    public class AdminController : Controller
    {
        private readonly UserManager<AppUser> _userManager;
        private readonly IChannelRepository _channelRepository;
        private readonly AppDbContext _context;
        private readonly IAlertEventService _alertEventService;
        private readonly IMediaServerClient _mediaServer;
        private readonly IStreamLifecycleService _lifecycle;
        private readonly IHubContext<StreamHub> _hub;
        private readonly MediaServerSettings _mediaSettings;
        private readonly StreamSimulator _streamSimulator;

        public AdminController(
            UserManager<AppUser> userManager,
            IChannelRepository channelRepository,
            AppDbContext context,
            IAlertEventService alertEventService,
            IMediaServerClient mediaServer,
            IStreamLifecycleService lifecycle,
            IHubContext<StreamHub> hub,
            IOptions<MediaServerSettings> mediaSettings,
            StreamSimulator streamSimulator)
        {
            _userManager = userManager;
            _channelRepository = channelRepository;
            _context = context;
            _alertEventService = alertEventService;
            _mediaServer = mediaServer;
            _lifecycle = lifecycle;
            _hub = hub;
            _mediaSettings = mediaSettings.Value;
            _streamSimulator = streamSimulator;
        }

        public async Task<IActionResult> Index()
        {
            ViewBag.TotalUsers = await _userManager.Users.CountAsync();
            ViewBag.TotalChannels = await _context.Channels.CountAsync();
            ViewBag.LiveChannels = await _context.Channels.CountAsync(c => c.IsLive);
            ViewBag.TotalDonations = await _context.Donations.CountAsync();
            return View();
        }

        public async Task<IActionResult> Users()
        {
            var users = await _userManager.Users
                .Include(u => u.Channel)
                .OrderByDescending(u => u.CreatedAt)
                .ToListAsync();

            ViewBag.Users = users;
            return View();
        }

        public async Task<IActionResult> Channels()
        {
            var channels = await _context.Channels
                .Include(c => c.User)
                .Include(c => c.Category)
                .OrderByDescending(c => c.IsLive)
                .ThenByDescending(c => c.ViewerCount)
                .ToListAsync();

            ViewBag.Channels = channels;
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> BanUser(string userId, string reason)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return NotFound();

            await _userManager.SetLockoutEnabledAsync(user, true);
            await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);

            var channel = await _channelRepository.GetByUserIdAsync(userId);
            if (channel != null)
            {
                // Si estaba transmitiendo, se corta. MediaMTX ya no lo dejará volver a publicar
                // (AuthorizePublishAsync rechaza a dueños suspendidos).
                if (channel.IsLive) await EndStreamInternalAsync(channel.Id);

                try
                {
                    await _alertEventService.CreateAlertAsync(
                        channelId: channel.Id.ToString(),
                        type: "UserBanned",
                        fromUserName: "Admin",
                        message: reason,
                        amount: 0);
                }
                catch { /* Mongo caído: el ban en SQL ya quedó aplicado */ }
            }

            TempData["Success"] = $"Usuario {user.DisplayUsername} baneado";
            return RedirectToAction("Users");
        }

        [HttpPost]
        public async Task<IActionResult> UnbanUser(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return NotFound();

            await _userManager.SetLockoutEndDateAsync(user, null);

            TempData["Success"] = $"Usuario {user.DisplayUsername} desbaneado";
            return RedirectToAction("Users");
        }

        // Reemplaza a "Marcar en vivo / offline": el estado EN VIVO ya no se inventa a mano,
        // sale de MediaMTX. Un admin sí puede cortar una transmisión (expulsa a OBS).
        [HttpPost]
        public async Task<IActionResult> EndStream(int channelId)
        {
            var channel = await _channelRepository.GetByIdAsync(channelId);
            if (channel == null) return NotFound();

            await EndStreamInternalAsync(channelId);

            TempData["Success"] = $"Transmisión de {channel.Name} terminada";
            return RedirectToAction("Channels");
        }

        private async Task EndStreamInternalAsync(int channelId)
        {
            // Si era un stream simulado, se apaga ffmpeg (si no, volvería a conectarse solo).
            await _streamSimulator.StopAsync(channelId);
            await _mediaServer.KickPublisherAsync(_mediaSettings.PathFor(channelId));
            var change = await _lifecycle.MarkOfflineAsync(channelId);
            if (change?.Changed == true)
                await _hub.Clients.Group(StreamHub.ChannelGroup(channelId)).SendAsync("StreamEnded");
        }

        [HttpPost]
        public async Task<IActionResult> ChangeUserRole(string userId, string role)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return NotFound();

            var currentRoles = await _userManager.GetRolesAsync(user);
            await _userManager.RemoveFromRolesAsync(user, currentRoles);
            await _userManager.AddToRoleAsync(user, role);

            TempData["Success"] = $"Rol de {user.DisplayUsername} cambiado a {role}";
            return RedirectToAction("Users");
        }
    }
}