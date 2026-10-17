using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

//using TwitchClonePorSeparadoConMongoMasSusurros.Data;
//using TwitchClonePorSeparadoConMongoMasSusurros.Models;

using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices;
using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    // Sin este prefijo, [HttpGet("{username}")] quedaba en la raíz y capturaba CUALQUIER
    // dirección de un segmento (/Admin, /Simulator, /Whisper...) como si fuera un perfil → 404.
    // Las rutas por atributo le ganan a la convencional {controller}/{action}.
    [Route("Profile")]
    public class ProfileController : Controller
    {
        private readonly AppDbContext _db;
        private readonly UserManager<AppUser> _userManager;

        public ProfileController(AppDbContext db, UserManager<AppUser> userManager)
        {
            _db = db;
            _userManager = userManager;
        }

        // GET: /Profile/{username}
        [HttpGet("{username}")]
        public async Task<IActionResult> Index(string username)
        {
            var user = await _userManager.FindByNameAsync(username);
            if (user == null) return NotFound();

            var channel = await _db.Channels
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.UserId == user.Id);

            var follows = await _db.Follows
                .Where(f => f.FollowerId == user.Id)
                .Include(f => f.Followed)
                .ToListAsync();

            var followerCount = await _db.Follows
                .Where(f => f.FollowedId == user.Id)
                .CountAsync();

            var currentUser = await _userManager.GetUserAsync(User);

            ViewBag.User = user;
            ViewBag.Channel = channel;
            ViewBag.Follows = follows;
            ViewBag.FollowerCount = followerCount;
            ViewBag.IsOwnProfile = currentUser?.Id == user.Id;

            return View(channel);
        }

        // GET: /Profile/My
        [HttpGet("My")]
        public async Task<IActionResult> MyProfile()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToAction("Login", "Account");

            return RedirectToAction("Index", new { username = user.UserName });
        }

        // GET: /Profile/Stats/{username}
        [HttpGet("Stats/{username}")]
        public async Task<IActionResult> GetStats(string username)
        {
            var user = await _userManager.FindByNameAsync(username);
            if (user == null) return NotFound();

            var channel = await _db.Channels
                .FirstOrDefaultAsync(c => c.UserId == user.Id);

            if (channel == null) return NotFound();

            var followerCount = await _db.Follows
                .Where(f => f.FollowedId == user.Id)
                .CountAsync();

            var stats = new
            {
                Username = user.DisplayUsername,
                ChannelName = channel.Name,
                Followers = followerCount,
                IsLive = channel.IsLive,
                CreatedAt = channel.CreatedAt,
                TotalClips = await _db.Clips.Where(c => c.ChannelId == channel.Id).CountAsync()
            };

            return Ok(stats);
        }
    }
}