using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    // Susurros: mensajes privados 1:1 entre usuarios, persistidos en MongoDB
    // (WhisperService), con entrega en tiempo real vía SignalR cuando el
    // destinatario está conectado. Equivalente a los "whispers" de Twitch.
    [Authorize]
    public class WhisperController : Controller
    {
        private const int MaxMessageLength = 500;

        private readonly IWhisperService _whisperService;
        private readonly UserManager<AppUser> _userManager;
        private readonly IHubContext<StreamHub> _hubContext;
        private readonly ILogger<WhisperController> _logger;

        public WhisperController(
            IWhisperService whisperService,
            UserManager<AppUser> userManager,
            IHubContext<StreamHub> hubContext,
            ILogger<WhisperController> logger)
        {
            _whisperService = whisperService;
            _userManager = userManager;
            _hubContext = hubContext;
            _logger = logger;
        }

        // GET: /Whisper — bandeja de conversaciones (tipo inbox)
        public async Task<IActionResult> Index()
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return RedirectToAction("Login", "Account");

            List<WhisperConversationSummary> conversations;
            try
            {
                conversations = await _whisperService.GetConversationsForUserAsync(currentUser.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudieron cargar las conversaciones de susurros. ¿Está MongoDB corriendo?");
                conversations = new List<WhisperConversationSummary>();
                ViewBag.WhispersUnavailable = true;
            }

            return View(conversations);
        }

        // GET: /Whisper/Conversation/{otherUserId}
        // Se accede por Id de usuario (no por username) porque es el dato que
        // ya tenemos disponible desde el botón "Susurrar" del perfil/canal, y
        // porque es estable aunque el usuario cambie su nombre público.
        public async Task<IActionResult> Conversation(string otherUserId)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return RedirectToAction("Login", "Account");

            if (string.IsNullOrWhiteSpace(otherUserId) || otherUserId == currentUser.Id)
                return BadRequest();

            var otherUser = await _userManager.FindByIdAsync(otherUserId);
            if (otherUser == null) return NotFound();

            var conversationId = WhisperMessage.BuildConversationId(currentUser.Id, otherUserId);

            List<WhisperMessage> messages;
            try
            {
                messages = await _whisperService.GetConversationAsync(conversationId);
                await _whisperService.MarkConversationAsReadAsync(conversationId, currentUser.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo cargar la conversación {ConversationId}. ¿Está MongoDB corriendo?", conversationId);
                messages = new List<WhisperMessage>();
                ViewBag.WhispersUnavailable = true;
            }

            ViewBag.OtherUser = otherUser;
            ViewBag.ConversationId = conversationId;
            ViewBag.CurrentUserId = currentUser.Id;

            return View(messages);
        }

        // POST: /Whisper/Send
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Send(string recipientId, string content)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return Unauthorized();

            if (string.IsNullOrWhiteSpace(recipientId) || recipientId == currentUser.Id)
                return BadRequest("Destinatario inválido");

            if (string.IsNullOrWhiteSpace(content))
                return BadRequest("El mensaje no puede estar vacío");

            content = content.Trim();
            if (content.Length > MaxMessageLength)
                content = content[..MaxMessageLength];

            var recipient = await _userManager.FindByIdAsync(recipientId);
            if (recipient == null) return NotFound("El destinatario no existe");

            try
            {
                var whisper = await _whisperService.SendAsync(
                    currentUser.Id, currentUser.DisplayUsername,
                    recipientId, recipient.DisplayUsername,
                    content);

                // Push en tiempo real si el destinatario está conectado ahora mismo.
                // Si no lo está, el mensaje ya quedó guardado y lo va a ver al
                // entrar a su bandeja — la entrega en vivo es un plus, no la
                // única vía de entrega.
                await _hubContext.Clients.User(recipientId)
                    .SendAsync("WhisperReceived", whisper.ConversationId, currentUser.DisplayUsername, content);

                return RedirectToAction("Conversation", new { otherUserId = recipientId });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo enviar el susurro de {SenderId} a {RecipientId}. ¿Está MongoDB corriendo?", currentUser.Id, recipientId);
                TempData["WhisperError"] = "No se pudo enviar el mensaje. Intenta de nuevo en unos segundos.";
                return RedirectToAction("Conversation", new { otherUserId = recipientId });
            }
        }

        // GET: /Whisper/UnreadCount — para el badge del navbar (llamado por fetch)
        [HttpGet]
        public async Task<IActionResult> UnreadCount()
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return Unauthorized();

            try
            {
                var count = await _whisperService.GetUnreadCountAsync(currentUser.Id);
                return Ok(new { count });
            }
            catch
            {
                return Ok(new { count = 0 });
            }
        }
    }
}
