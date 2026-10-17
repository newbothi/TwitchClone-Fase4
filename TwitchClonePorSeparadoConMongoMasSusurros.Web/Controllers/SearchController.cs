using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

//using TwitchClonePorSeparadoConMongoMasSusurros.Data;

using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices;
using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    public class SearchController : Controller
    {
        private readonly AppDbContext _db;

        public SearchController(AppDbContext db)
        {
            _db = db;
        }

        // GET: /Search
        // ?all=true muestra hasta 200 resultados (enlace "Ver más resultados"); si no, solo 20.
        [HttpGet]
        public async Task<IActionResult> Index(string q = "", bool all = false)
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return View(new List<TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.Channel>());
            }

            var query = q.ToLower().Trim();
            const int limitedCount = 20;
            const int allCount = 200;
            var take = all ? allCount : limitedCount;

            var matchingQuery = _db.Channels
                .Where(c => c.Name.ToLower().Contains(query) ||
                           c.Description.ToLower().Contains(query) ||
                           c.User.DisplayUsername.ToLower().Contains(query));

            var totalMatches = await matchingQuery.CountAsync();

            var channels = await matchingQuery
                .Include(c => c.User)
                .OrderByDescending(c => c.IsLive)
                .ThenByDescending(c => c.CreatedAt)
                .Take(take)
                .ToListAsync();

            ViewBag.Query = q;
            ViewBag.ResultCount = channels.Count;
            ViewBag.TotalMatches = totalMatches;
            ViewBag.ShowingAll = all;
            // Solo tiene sentido ofrecer "ver más" si hay más resultados de los que ya se muestran
            ViewBag.HasMore = !all && totalMatches > channels.Count;
            return View(channels);
        }

        // GET: /Search/Api
        [HttpGet("search/api")]
        public async Task<IActionResult> ApiSearch([FromQuery] string q)
        {
            if (string.IsNullOrWhiteSpace(q))
                return Ok(new List<object>());

            var query = q.ToLower().Trim();

            var channels = await _db.Channels
                .Where(c => c.Name.ToLower().Contains(query) ||
                           c.User.DisplayUsername.ToLower().Contains(query))
                .Include(c => c.User)
                .OrderByDescending(c => c.IsLive)
                .Take(10)
                .ToListAsync();

            var result = channels.Select(c => new
            {
                c.Id,
                c.Name,
                c.IsLive,
                Streamer = c.User.DisplayUsername
            });

            return Ok(result);
        }
    }
}