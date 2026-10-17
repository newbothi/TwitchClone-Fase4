using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    public class AccountController : Controller
    {
        private readonly UserManager<AppUser> _userManager;
        private readonly SignInManager<AppUser> _signInManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IUserService _userService;
        private readonly IChannelService _channelService;
        private readonly AppDbContext _context;

        public AccountController(
            UserManager<AppUser> userManager,
            SignInManager<AppUser> signInManager,
            RoleManager<IdentityRole> roleManager,
            IUserService userService,
            IChannelService channelService,
            AppDbContext context)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _userService = userService;
            _channelService = channelService;
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Register()
        {
            ViewBag.Categories = await _context.Categories.Where(c => c.ParentCategoryId == null).OrderBy(c => c.Name).ToListAsync();
            return View(new RegisterDto());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterDto model)
        {
            // Solo permitir Viewer o Streamer al registrarse
            if (model.Role != "Viewer" && model.Role != "Streamer")
                model.Role = "Viewer";

            // Si es Streamer, la categoría del stream es obligatoria
            if (model.Role == "Streamer" && model.CategoryId == null)
                ModelState.AddModelError(nameof(model.CategoryId), "Selecciona la categoría de tu stream");

            if (!ModelState.IsValid)
            {
                ViewBag.Categories = await _context.Categories.Where(c => c.ParentCategoryId == null).OrderBy(c => c.Name).ToListAsync();
                return View(model);
            }

            var result = await _userService.RegisterAsync(model);

            if (result.Succeeded)
            {
                // Obtener usuario para confirmar creación
                var user = await _userManager.FindByEmailAsync(model.Email);
                if (user != null)
                {
                    // Si es Streamer, crear su canal automáticamente con la categoría elegida
                    if (model.Role == "Streamer")
                    {
                        await _channelService.CreateChannelAsync(user.Id, user.DisplayUsername, model.CategoryId);
                    }

                    await _signInManager.SignInAsync(user, isPersistent: false);
                }

                return RedirectToAction("Index", "Home");
            }

            // Mostrar errores de Identity
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);

            ViewBag.Categories = await _context.Categories.Where(c => c.ParentCategoryId == null).OrderBy(c => c.Name).ToListAsync();
            return View(model);
        }

        [HttpGet]
        public IActionResult Login()
        {
            return View(new LoginDto());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginDto model)
        {
            if (!ModelState.IsValid) return View(model);

            var success = await _userService.LoginAsync(model);

            if (success)
                return RedirectToAction("Index", "Home");

            // Verificar si cuenta está bloqueada
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user != null && await _userManager.IsLockedOutAsync(user))
            {
                ModelState.AddModelError(string.Empty, "Cuenta bloqueada temporalmente. Intenta más tarde.");
                return View(model);
            }

            ModelState.AddModelError(string.Empty, "Email o contraseña incorrectos.");
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await _userService.LogoutAsync();
            return RedirectToAction("Index", "Home");
        }

        public IActionResult AccessDenied()
        {
            return View();
        }
    }
}