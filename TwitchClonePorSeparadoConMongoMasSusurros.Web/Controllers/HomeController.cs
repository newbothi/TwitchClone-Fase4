using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly IChannelService _channelService;

        public HomeController(ILogger<HomeController> logger, IChannelService channelService)
        {
            _logger = logger;
            _channelService = channelService;
        }

        public async Task<IActionResult> Index()
        {
            var liveChannels = await _channelService.GetLiveChannelsAsync();
            return View(liveChannels?.ToList() ?? new List<ChannelDto>());
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}