using Affiliate.Data;
using Affiliate.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Controllers
{
    /// <summary>Manual links between Amazon and Noon products, with a price comparison.</summary>
    public class ProductLinksController(AffiliateDbContext context) : Controller
    {
        private const int SearchLimit = 20;

        /// <param name="cheaper">"amazon" or "noon" to show only links where that store is cheaper.</param>
        public async Task<IActionResult> Index(string? cheaper)
        {
            var query = context.ProductLinks.AsNoTracking();
            query = cheaper switch
            {
                "amazon" => query.Where(l => l.Product!.CurrentPrice < l.NoonProduct!.CurrentPrice),
                "noon" => query.Where(l => l.NoonProduct!.CurrentPrice < l.Product!.CurrentPrice),
                _ => query
            };

            ViewData["Cheaper"] = cheaper;
            return View(await query
                .Include(l => l.Product)
                .Include(l => l.NoonProduct)
                .OrderByDescending(l => l.CreatedAt)
                .ToListAsync());
        }

        public IActionResult Create() => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(int productId, int noonProductId)
        {
            var taken = await context.ProductLinks.AnyAsync(l => l.ProductId == productId || l.NoonProductId == noonProductId);
            var exists = await context.Products.AnyAsync(p => p.Id == productId)
                         && await context.NoonProducts.AnyAsync(p => p.Id == noonProductId);

            if (taken || !exists)
            {
                TempData["StatusMessage"] = taken ? "أحد المنتجين مربوط بالفعل." : "اختر منتج أمازون ومنتج نون.";
                return RedirectToAction(nameof(Create));
            }

            context.ProductLinks.Add(new ProductLink { ProductId = productId, NoonProductId = noonProductId });
            await context.SaveChangesAsync();
            TempData["StatusMessage"] = "تم ربط المنتجين.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            await context.ProductLinks.Where(l => l.Id == id).ExecuteDeleteAsync();
            TempData["StatusMessage"] = "تم إلغاء الربط.";
            return RedirectToAction(nameof(Index));
        }

        /// <summary>Unlinked Amazon products matching a name or ASIN.</summary>
        public async Task<IActionResult> SearchAmazon(string q) =>
            Json(await context.Products.AsNoTracking()
                .Where(p => (p.Name.Contains(q) || p.Asin == q) && !context.ProductLinks.Any(l => l.ProductId == p.Id))
                .OrderBy(p => p.Name)
                .Take(SearchLimit)
                .Select(p => new { p.Id, p.Name, Code = p.Asin, Price = p.CurrentPrice })
                .ToListAsync());

        /// <summary>Unlinked Noon products matching a name or SKU.</summary>
        public async Task<IActionResult> SearchNoon(string q) =>
            Json(await context.NoonProducts.AsNoTracking()
                .Where(p => (p.Name.Contains(q) || p.Sku == q) && !context.ProductLinks.Any(l => l.NoonProductId == p.Id))
                .OrderBy(p => p.Name)
                .Take(SearchLimit)
                .Select(p => new { p.Id, p.Name, Code = p.Sku, Price = p.CurrentPrice })
                .ToListAsync());
    }
}
