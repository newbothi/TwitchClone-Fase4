using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

//using TwitchClonePorSeparadoConMongoMasSusurros.Data;
//using TwitchClonePorSeparadoConMongoMasSusurros.Models;

//using TwitchClonePorSeparadoConMongoMasSusurros.Services;
using Microsoft.EntityFrameworkCore;

using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    public class ClipController : Controller
    {
        private readonly ClipService _clip;
        private readonly UserManager<AppUser> _userManager;
        private readonly AppDbContext _db;

        public ClipController(
            ClipService clip,
            UserManager<AppUser> userManager,
            AppDbContext db)
        {
            _clip = clip;
            _userManager = userManager;
            _db = db;
        }

        // GET: /Clip/Channel/5
        public async Task<IActionResult> Channel(int id)
        {
            var clips = await _clip.GetClipsByChannelAsync(id);
            ViewBag.ChannelId = id;
            return View(clips);
        }

        // GET: /Clip/Watch/5
        public async Task<IActionResult> Watch(int id)
        {
            var clip = await _clip.GetClipByIdAsync(id);
            if (clip == null) return NotFound();

            // Incrementar vistas
            await _clip.IncrementViewsAsync(id);

            return View(clip);
        }

        // GET: /Clip/Popular
        public async Task<IActionResult> Popular()
        {
            var clips = await _clip.GetPopularClipsAsync();
            return View(clips);
        }

        // POST: /Clip/Create
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(int channelId, string title)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToAction("Login", "Account");

            var channel = await _db.Channels.FindAsync(channelId);
            if (channel == null || !channel.IsLive)
            {
                TempData["Error"] = "Solo puedes crear clips cuando el canal está en vivo.";
                return RedirectToAction("Watch", "Channel", new { id = channelId });
            }

            // TODO: Generar video del clip desde el stream actual
            var videoUrl = $"/clips/pending/{channelId}_{Guid.NewGuid()}.mp4";
            var durationSeconds = 60; // Duración default del clip

            var clip = await _clip.CreateClipAsync(channelId, user.Id, title, videoUrl, durationSeconds);

            TempData["Success"] = "¡Clip creado!";
            return RedirectToAction("Watch", new { id = clip.Id });
        }

        // POST: /Clip/Delete
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int clipId, int channelId)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToAction("Login", "Account");

            var clip = await _clip.GetClipByIdAsync(clipId);
            if (clip == null) return NotFound();

            // Verificar que el usuario sea el creador o dueño del canal
            if (clip.CreatedByUserId != user.Id && clip.Channel?.UserId != user.Id)
                return Unauthorized();

            await _clip.DeleteClipAsync(clipId);

            TempData["Success"] = "Clip eliminado";
            return RedirectToAction("Channel", new { id = channelId });
        }
    }
}