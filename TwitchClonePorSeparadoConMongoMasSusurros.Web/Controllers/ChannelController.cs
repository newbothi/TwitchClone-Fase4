using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    public class ChannelController : Controller
    {
        private const long MaxEmoteBytes = 1024 * 1024; // 1 MB
        private static readonly Regex EmoteCodeRegex = new("^[A-Za-z0-9_]{2,25}$", RegexOptions.Compiled);
        private static readonly HashSet<string> AllowedEmoteExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".gif", ".webp" };
        private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

        private readonly IChannelService _channelService;
        private readonly IChannelRepository _channelRepository;
        private readonly UserManager<AppUser> _userManager;
        private readonly IChatService _chat;
        private readonly IModerationService _moderation;
        private readonly IFollowService _followService;
        private readonly IMediaServerClient _mediaServer;
        private readonly ChatEffectDispatcher _dispatcher;
        private readonly IChannelRoleRepository _roles;
        private readonly IChatUserRepository _chatUsers;
        private readonly MediaServerSettings _mediaSettings;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<ChannelController> _logger;

        public ChannelController(
            IChannelService channelService,
            IChannelRepository channelRepository,
            UserManager<AppUser> userManager,
            IChatService chat,
            IModerationService moderation,
            IFollowService followService,
            IMediaServerClient mediaServer,
            ChatEffectDispatcher dispatcher,
            IChannelRoleRepository roles,
            IChatUserRepository chatUsers,
            IOptions<MediaServerSettings> mediaSettings,
            IWebHostEnvironment env,
            ILogger<ChannelController> logger)
        {
            _channelService = channelService;
            _channelRepository = channelRepository;
            _userManager = userManager;
            _chat = chat;
            _moderation = moderation;
            _followService = followService;
            _mediaServer = mediaServer;
            _dispatcher = dispatcher;
            _roles = roles;
            _chatUsers = chatUsers;
            _mediaSettings = mediaSettings.Value;
            _env = env;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Watch(int id)
        {
            var channel = await _channelRepository.GetByIdAsync(id);
            if (channel == null) return NotFound();

            var dto = new ChannelDto
            {
                Id = channel.Id,
                UserId = channel.UserId,
                UserName = channel.User?.UserName ?? string.Empty,
                DisplayUsername = channel.User?.DisplayUsername ?? string.Empty,
                Name = channel.Name,
                Description = channel.Description,
                IsLive = channel.IsLive,
                ViewerCount = channel.ViewerCount,
                CategoryName = channel.Category?.Name
            };

            // Historial del chat desde MongoDB, ya en orden cronológico y en el mismo
            // formato que llega por SignalR (se pinta con el mismo código JS).
            var chatUnavailable = false;
            List<ChatMessageDto> history;
            try
            {
                history = await _chat.GetHistoryAsync(channel.Id, 50);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo cargar el historial de chat del canal {ChannelId}. ¿Está MongoDB corriendo?", channel.Id);
                history = new List<ChatMessageDto>();
                chatUnavailable = true;
            }

            var currentUser = User.Identity!.IsAuthenticated ? await _userManager.GetUserAsync(User) : null;
            var sender = currentUser != null ? new ChatSender(currentUser.Id, User.IsInRole("Admin")) : null;

            ViewBag.IsOwnChannel = currentUser != null && currentUser.Id == channel.UserId;
            ViewBag.IsFollowing = currentUser != null && !(bool)ViewBag.IsOwnChannel
                ? await _followService.IsFollowingAsync(currentUser.Id, channel.UserId)
                : false;
            ViewBag.FollowerCount = await _followService.GetFollowerCountAsync(channel.UserId);

            // La URL pública usa el Id del canal. La stream key ya no aparece en la página.
            var config = new WatchPageConfig
            {
                ChannelId = channel.Id,
                HlsUrl = _mediaSettings.HlsUrlFor(channel.Id),
                IsLive = channel.IsLive,
                IsAuthenticated = currentUser != null,
                CurrentUserId = currentUser?.Id,
                CurrentDisplayName = currentUser?.DisplayUsername,
                CanModerate = sender != null && await _chat.CanModerateAsync(channel.Id, sender),
                ChatUnavailable = chatUnavailable,
                History = history
            };
            ViewBag.WatchConfigJson = JsonSerializer.Serialize(config, JsonWeb);

            return View(dto);
        }

        [HttpGet]
        [Authorize(Roles = "Streamer")]
        public async Task<IActionResult> Dashboard()
        {
            var userId = _userManager.GetUserId(User)!;
            var channel = await _channelService.GetByUserIdAsync(userId);
            if (channel == null) return RedirectToAction("Create");

            var streamKey = await _channelService.GetStreamKeyAsync(userId) ?? string.Empty;
            ViewBag.ObsServer = _mediaSettings.IngestUrl;
            ViewBag.ObsStreamKey = MediaServerSettings.ObsStreamKeyFor(channel.Id, streamKey);
            ViewBag.Emotes = (await _channelRepository.GetByIdAsync(channel.Id))?.Emotes.OrderBy(e => e.Code).ToList()
                             ?? new List<Emote>();

            try
            {
                ViewBag.ChatSettings = await _chat.GetModerationSettingsAsync(channel.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo leer la configuración del chat del canal {ChannelId}.", channel.Id);
                ViewBag.ChatSettings = null;
            }

            ViewBag.BannedUsers = await _moderation.GetBannedUsersAsync(channel.Id);
            try
            {
                ViewBag.ChannelRoles = await _roles.ListAsync(channel.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudieron leer los moderadores/VIP del canal {ChannelId}.", channel.Id);
                ViewBag.ChannelRoles = new List<ChannelRoleEntry>();
            }
            ViewBag.AutoModProvider = HttpContext.RequestServices.GetRequiredService<IAutoModClassifier>() is { IsAvailable: true } classifier
                ? classifier.ProviderName
                : null;

            TempData.Keep();
            return View(channel);
        }

        // Fase 4 (c): datos de los gráficos del Dashboard (JSON). Solo del canal propio.
        // Los lee de analytics.*, que llena el servicio python/analytics.
        [HttpGet]
        [Authorize(Roles = "Streamer")]
        public async Task<IActionResult> Analytics([FromServices] IChannelAnalyticsService analytics, int days = 30)
        {
            var channel = await GetOwnChannelAsync();
            if (channel == null) return NotFound();

            try
            {
                return Json(await analytics.GetAsync(channel.Id, days));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudieron leer las estadísticas del canal {ChannelId}.", channel.Id);
                return Json(new ChannelAnalyticsDto
                {
                    Days = days,
                    Message = "No se pudieron leer las estadísticas. ¿Está SQL Server disponible?"
                });
            }
        }

        [HttpGet]
        [Authorize(Roles = "Streamer")]
        public IActionResult Create()
        {
            return View();
        }

        [HttpPost]
        [Authorize(Roles = "Streamer")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string name, string description)
        {
            var userId = _userManager.GetUserId(User)!;
            var user = await _userManager.GetUserAsync(User);
            await _channelService.CreateChannelAsync(userId, user!.DisplayUsername);
            return RedirectToAction("Dashboard");
        }

        [HttpPost]
        [Authorize(Roles = "Streamer")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Update(string title, string description)
        {
            // Solo se edita el canal del usuario logueado (antes se aceptaba cualquier channelId).
            var userId = _userManager.GetUserId(User)!;
            var ok = await _channelService.UpdateChannelAsync(userId, title, description);

            if (ok) TempData["Success"] = "Canal actualizado correctamente";
            else TempData["Error"] = "El título no puede estar vacío.";
            return RedirectToAction("Dashboard");
        }

        [HttpPost]
        [Authorize(Roles = "Streamer")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegenerateKey()
        {
            var userId = _userManager.GetUserId(User)!;
            var channelId = await _channelService.RegenerateStreamKeyAsync(userId);
            if (channelId == null) return RedirectToAction("Create");

            // Si OBS estaba transmitiendo con la clave vieja, se lo desconecta.
            // Al reintentar con la clave vieja, MediaMTX lo rechazará.
            var kicked = await _mediaServer.KickPublisherAsync(_mediaSettings.PathFor(channelId.Value));

            TempData["Success"] = kicked
                ? "Stream key regenerada. Tu transmisión actual se cortó: actualiza la clave en OBS."
                : "Stream key regenerada. Actualiza la clave en OBS.";
            return RedirectToAction("Dashboard");
        }

        // ToggleLive fue eliminado: IsLive lo controla MediaMTX (MediaHooksController +
        // LiveStateReconciler) según si OBS está publicando de verdad.

        [HttpPost]
        [Authorize(Roles = "Streamer")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChatSettings(int slowModeSeconds, bool followersOnly, bool emoteOnly,
            bool blockLinks, bool autoModEnabled, string? rules, string? bannedWords,
            bool blockExcessiveCaps = false, bool blockRepeatedChars = false)
        {
            var channel = await GetOwnChannelAsync();
            if (channel == null) return RedirectToAction("Create");

            var words = (bannedWords ?? string.Empty)
                .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            try
            {
                var result = await _chat.UpdateSettingsAsync(channel.Id, new ChatModerationSettingsDto
                {
                    SlowModeSeconds = slowModeSeconds,
                    FollowersOnly = followersOnly,
                    EmoteOnly = emoteOnly,
                    BlockLinks = blockLinks,
                    AutoModEnabled = autoModEnabled,
                    BlockExcessiveCaps = blockExcessiveCaps,
                    BlockRepeatedChars = blockRepeatedChars,
                    Rules = rules ?? string.Empty,
                    BannedWords = words.ToList()
                });
                await _dispatcher.DispatchAsync(channel.Id, result.Effects);
                TempData["Success"] = "Configuración del chat guardada";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo guardar la configuración del chat del canal {ChannelId}.", channel.Id);
                TempData["Error"] = "No se pudo guardar la configuración del chat. ¿Está MongoDB corriendo?";
            }

            return RedirectToAction("Dashboard");
        }

        [HttpPost]
        [Authorize(Roles = "Streamer")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Unban(string userId)
        {
            var channel = await GetOwnChannelAsync();
            if (channel == null) return RedirectToAction("Create");

            var result = await _chat.UnbanAsync(channel.Id, userId, CurrentSender());
            await _dispatcher.DispatchAsync(channel.Id, result.Effects);

            if (result.Ok) TempData["Success"] = $"{result.TargetDisplayName ?? "El usuario"} ya puede volver a chatear";
            else TempData["Error"] = result.Error;

            return RedirectToAction("Dashboard");
        }

        // Nombrar moderador o VIP escribiendo su nombre público (igual que /mod o /vip en el chat).
        [HttpPost]
        [Authorize(Roles = "Streamer")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddRole(string displayName, string role)
        {
            var channel = await GetOwnChannelAsync();
            if (channel == null) return RedirectToAction("Create");

            var target = await _chatUsers.FindByDisplayNameAsync((displayName ?? string.Empty).TrimStart('@'));
            if (target == null)
            {
                TempData["Error"] = $"No encontré al usuario «{displayName}».";
                return RedirectToAction("Dashboard");
            }

            var roleType = role == "vip" ? ChannelRoleType.Vip : ChannelRoleType.Moderator;
            var result = await _chat.SetRoleAsync(channel.Id, target.Id, roleType, CurrentSender());
            await _dispatcher.DispatchAsync(channel.Id, result.Effects);

            if (result.Ok) TempData["Success"] = result.Notice;
            else TempData["Error"] = result.Error;
            return RedirectToAction("Dashboard");
        }

        [HttpPost]
        [Authorize(Roles = "Streamer")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveRole(string userId)
        {
            var channel = await GetOwnChannelAsync();
            if (channel == null) return RedirectToAction("Create");

            var result = await _chat.SetRoleAsync(channel.Id, userId, ChannelRoleType.None, CurrentSender());
            await _dispatcher.DispatchAsync(channel.Id, result.Effects);

            if (result.Ok) TempData["Success"] = result.Notice;
            else TempData["Error"] = result.Error;
            return RedirectToAction("Dashboard");
        }

        private ChatSender CurrentSender() => new(_userManager.GetUserId(User)!, User.IsInRole("Admin"));

        [HttpPost]
        [Authorize(Roles = "Streamer")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadEmote(string code, IFormFile image)
        {
            var channel = await GetOwnChannelAsync(includeEmotes: true);
            if (channel == null) return RedirectToAction("Create");

            code = (code ?? string.Empty).Trim().Trim(':');
            if (!EmoteCodeRegex.IsMatch(code))
                return EmoteError("El nombre del emote debe tener entre 2 y 25 caracteres: letras, números o guion bajo.");

            if (image == null || image.Length == 0)
                return EmoteError("Debes seleccionar una imagen.");
            if (image.Length > MaxEmoteBytes)
                return EmoteError("La imagen no puede pesar más de 1 MB.");

            var extension = Path.GetExtension(image.FileName);
            if (!AllowedEmoteExtensions.Contains(extension) || !await HasImageSignatureAsync(image))
                return EmoteError("Solo se permiten imágenes PNG, GIF o WEBP.");

            if (channel.Emotes.Any(e => string.Equals(e.Code.Trim(':'), code, StringComparison.Ordinal)))
                return EmoteError($"Ya tienes un emote llamado {code}.");

            // El nombre del archivo lo genera el servidor: el texto del usuario nunca forma parte de la ruta.
            var uploadsDir = Path.Combine(_env.WebRootPath, "emotes");
            Directory.CreateDirectory(uploadsDir);
            var fileName = $"{channel.Id}_{Guid.NewGuid():N}{extension.ToLowerInvariant()}";

            await using (var stream = new FileStream(Path.Combine(uploadsDir, fileName), FileMode.CreateNew))
                await image.CopyToAsync(stream);

            channel.Emotes.Add(new Emote
            {
                ChannelId = channel.Id,
                Code = code,
                ImageUrl = $"/emotes/{fileName}",
                CreatedAt = DateTime.Now
            });
            await _channelRepository.UpdateAsync(channel);
            _chat.InvalidateChannel(channel.Id);

            TempData["Success"] = $"Emote {code} subido. Escríbelo en el chat como {code} o :{code}:";
            return RedirectToAction("Dashboard");
        }

        [HttpPost]
        [Authorize(Roles = "Streamer")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteEmote(int emoteId)
        {
            // Antes el formulario no mandaba channelId y la acción siempre devolvía 404;
            // además no se verificaba que el canal fuera del usuario.
            var channel = await GetOwnChannelAsync(includeEmotes: true);
            if (channel == null) return RedirectToAction("Create");

            var emote = channel.Emotes.FirstOrDefault(e => e.Id == emoteId);
            if (emote == null) return NotFound();

            channel.Emotes.Remove(emote);
            await _channelRepository.UpdateAsync(channel);
            _chat.InvalidateChannel(channel.Id);
            TryDeleteEmoteFile(emote.ImageUrl);

            TempData["Success"] = "Emote eliminado";
            return RedirectToAction("Dashboard");
        }

        // ── Utilidades ─────────────────────────────────────────────────────

        private async Task<Channel?> GetOwnChannelAsync(bool includeEmotes = false)
        {
            var userId = _userManager.GetUserId(User)!;
            var channel = await _channelRepository.GetByUserIdAsync(userId);
            if (channel == null || !includeEmotes) return channel;
            return await _channelRepository.GetByIdAsync(channel.Id);
        }

        private IActionResult EmoteError(string message)
        {
            TempData["EmoteError"] = message;
            return RedirectToAction("Dashboard");
        }

        private static async Task<bool> HasImageSignatureAsync(IFormFile file)
        {
            var header = new byte[12];
            await using var stream = file.OpenReadStream();
            var read = await stream.ReadAsync(header);
            if (read < 4) return false;

            var isPng = header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47;
            var isGif = header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38;
            var isWebp = read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
                         && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50;
            return isPng || isGif || isWebp;
        }

        private void TryDeleteEmoteFile(string imageUrl)
        {
            try
            {
                var emotesDir = Path.GetFullPath(Path.Combine(_env.WebRootPath, "emotes")) + Path.DirectorySeparatorChar;
                var fullPath = Path.GetFullPath(Path.Combine(_env.WebRootPath, imageUrl.TrimStart('/')));
                if (fullPath.StartsWith(emotesDir, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(fullPath))
                    System.IO.File.Delete(fullPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No se pudo borrar el archivo del emote {ImageUrl}.", imageUrl);
            }
        }
    }
}
