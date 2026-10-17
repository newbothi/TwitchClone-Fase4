using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    [Authorize]
    public class NotificationController : Controller
    {
        // Antes pedía la clase concreta NotificationService, que no está registrada en DI
        // (solo la interfaz), y /Notification daba error 500.
        private readonly INotificationService _notificationService;
        private readonly UserManager<AppUser> _userManager;

        public NotificationController(INotificationService notificationService, UserManager<AppUser> userManager)
        {
            _notificationService = notificationService;
            _userManager = userManager;
        }

      
        // GET: /Notification
        public async Task<IActionResult> Index()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            var notifications = await _notificationService.GetUserNotificationsAsync(user.Id);

            var unreadCount = notifications.Count(n => !n.IsRead);
            ViewBag.UnreadCount = unreadCount;

            return View(notifications);
        }

        // POST: /Notification/MarkAsRead/id
        [HttpPost]
        public async Task<IActionResult> MarkAsRead(string notificationId)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            await _notificationService.MarkAsReadAsync(notificationId);
            return Ok(new { success = true });
        }

        // POST: /Notification/MarkAllAsRead
        [HttpPost]
        public async Task<IActionResult> MarkAllAsRead()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            await _notificationService.MarkAllAsReadAsync(user.Id);
            return Ok(new { success = true });
        }
    }
}