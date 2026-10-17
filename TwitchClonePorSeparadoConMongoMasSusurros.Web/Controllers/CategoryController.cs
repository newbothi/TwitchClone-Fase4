using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers
{
    public class CategoryController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IChannelService _channelService;

        public CategoryController(AppDbContext context, IChannelService channelService)
        {
            _context = context;
            _channelService = channelService;
        }

        public async Task<IActionResult> Index()
        {
            var categories = await _context.Categories
                .Where(c => c.ParentCategoryId == null)
                .Include(c => c.CategoryTags)
                    .ThenInclude(ct => ct.Tag)
                .Include(c => c.SubCategories)
                .OrderBy(c => c.Name)
                .ToListAsync();

            return View(categories);
        }

        public async Task<IActionResult> Detail(int id)
        {
            var category = await _context.Categories
                .Include(c => c.CategoryTags)
                    .ThenInclude(ct => ct.Tag)
                .Include(c => c.SubCategories)
                .Include(c => c.ParentCategory)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (category == null) return NotFound();

            var channels = await _channelService.GetByCategoryAsync(id);

            ViewBag.Category = category;
            return View(channels.ToList());
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Create()
        {
            ViewBag.Tags = await _context.Tags.OrderBy(t => t.Name).ToListAsync();
            ViewBag.RootCategories = await _context.Categories
                .Where(c => c.ParentCategoryId == null)
                .OrderBy(c => c.Name)
                .ToListAsync();
            return View();
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Create(Category category, int[] tagIds)
        {
            if (!ModelState.IsValid)
            {
                ViewBag.Tags = await _context.Tags.OrderBy(t => t.Name).ToListAsync();
                ViewBag.RootCategories = await _context.Categories
                    .Where(c => c.ParentCategoryId == null)
                    .OrderBy(c => c.Name)
                    .ToListAsync();
                return View(category);
            }

            category.CreatedAt = DateTime.Now;
            await _context.Categories.AddAsync(category);
            await _context.SaveChangesAsync();

            if (tagIds != null)
            {
                foreach (var tagId in tagIds)
                {
                    _context.CategoryTags.Add(new CategoryTag { CategoryId = category.Id, TagId = tagId });
                }
                await _context.SaveChangesAsync();
            }

            TempData["Success"] = "Categoría creada correctamente";
            return RedirectToAction("Index");
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int id)
        {
            var category = await _context.Categories.FindAsync(id);
            if (category == null) return NotFound();

            _context.Categories.Remove(category);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Categoría eliminada";
            return RedirectToAction("Index");
        }
    }
}