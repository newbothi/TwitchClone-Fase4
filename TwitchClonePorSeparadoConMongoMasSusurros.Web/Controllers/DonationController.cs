using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    [Authorize]
    public class DonationController : Controller
    {
        private readonly AppDbContext _context;
        private readonly UserManager<AppUser> _userManager;
        private readonly IHubContext<StreamHub> _hubContext;
        private readonly IAlertEventService _alertEventService;

        public DonationController(
            AppDbContext context,
            UserManager<AppUser> userManager,
            IHubContext<StreamHub> hubContext,
            IAlertEventService alertEventService)
        {
            _context = context;
            _userManager = userManager;
            _hubContext = hubContext;
            _alertEventService = alertEventService;
        }

        [HttpGet]
        public async Task<IActionResult> Channel(int id)
        {
            var channel = await _context.Channels
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (channel == null) return NotFound();

            var currentUser = await _userManager.GetUserAsync(User);
            ViewBag.Channel = channel;
            ViewBag.UserPoints = currentUser?.Points ?? 0;

            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Channel(int channelId, decimal amount, string message)
        {
            var fromUser = await _userManager.GetUserAsync(User);
            if (fromUser == null) return Unauthorized();

            var channel = await _context.Channels
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.Id == channelId);

            if (channel == null) return NotFound();

            if (fromUser.Points < (int)amount)
            {
                TempData["Error"] = "No tienes suficientes puntos";
                return RedirectToAction("Channel", new { id = channelId });
            }

            // Descontar puntos en SQL
            fromUser.Points -= (int)amount;
            await _userManager.UpdateAsync(fromUser);

            // Guardar donación en SQL
            var donation = new Donation
            {
                FromUserId = fromUser.Id,
                ChannelId = channelId,
                Amount = amount,
                Message = message ?? string.Empty,
                CreatedAt = DateTime.Now
            };

            await _context.Donations.AddAsync(donation);
            await _context.SaveChangesAsync();

            // Alerta en MongoDB
            await _alertEventService.CreateAlertAsync(
                channelId: channelId.ToString(),
                type: "Donation",
                fromUserName: fromUser.DisplayUsername,
                message: message ?? string.Empty,
                amount: (int)amount);

            // Notificar via SignalR
            await _hubContext.Clients
                .Group($"channel_{channelId}")
                .SendAsync("DonationAlert", fromUser.DisplayUsername, (int)amount, message);

            TempData["Success"] = $"¡Donaste {amount} puntos a {channel.User?.DisplayUsername}!";
            return RedirectToAction("Watch", "Channel", new { id = channelId });
        }

        [HttpGet]
        public async Task<IActionResult> MyPoints()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            var donations = await _context.Donations
                .Include(d => d.Channel)
                    .ThenInclude(c => c!.User)
                .Where(d => d.FromUserId == user.Id)
                .OrderByDescending(d => d.CreatedAt)
                .Take(20)
                .ToListAsync();

            ViewBag.Points = user.Points;
            ViewBag.Donations = donations;

            return View();
        }

        [HttpGet]
        [Authorize(Roles = "Streamer,Admin")]
        public async Task<IActionResult> History(int channelId)
        {
            var donations = await _context.Donations
                .Include(d => d.FromUser)
                .Where(d => d.ChannelId == channelId)
                .OrderByDescending(d => d.CreatedAt)
                .ToListAsync();

            ViewBag.ChannelId = channelId;
            ViewBag.Total = donations.Sum(d => d.Amount);

            return View(donations);
        }
    }
}