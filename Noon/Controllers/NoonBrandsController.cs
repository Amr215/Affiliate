using Affiliate.Controllers;
using Affiliate.Data;
using Affiliate.Noon.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Noon.Controllers
{
    public class NoonBrandsController(
        AffiliateDbContext context,
        INoonScraperService scraper,
        NoonCatalogClient catalog) : Controller
    {
        public async Task<IActionResult> Index() =>
            View(await context.NoonBrandUrls.AsNoTracking().OrderBy(b => b.Name).ToListAsync());

        public async Task<IActionResult> Details(int id)
        {
            var brand = await context.NoonBrandUrls.FindAsync(id);
            return brand is null ? NotFound() : View(brand);
        }

        public IActionResult Create() => View(new NoonBrandUrl());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            [Bind(nameof(NoonBrandUrl.Name), nameof(NoonBrandUrl.Url), nameof(NoonBrandUrl.IsEnabled))] NoonBrandUrl brand,
            CancellationToken cancellationToken)
        {
            Validate(brand);
            if (!ModelState.IsValid)
                return View(brand);

            brand.CreatedAt = DateTime.UtcNow;
            context.Add(brand);
            await context.SaveChangesAsync(cancellationToken);

            if (brand.IsEnabled)
                return await RunNow(brand.Id, cancellationToken);

            TempData["StatusMessage"] = $"تمت إضافة \"{brand.Name}\".";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(int id)
        {
            var brand = await context.NoonBrandUrls.FindAsync(id);
            return brand is null ? NotFound() : View(brand);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            [Bind(nameof(NoonBrandUrl.Name), nameof(NoonBrandUrl.Url), nameof(NoonBrandUrl.IsEnabled))] NoonBrandUrl input)
        {
            var brand = await context.NoonBrandUrls.FindAsync(id);
            if (brand is null)
                return NotFound();

            input.Id = id;
            Validate(input);
            if (!ModelState.IsValid)
                return View(input);

            brand.Name = input.Name;
            brand.Url = input.Url;
            brand.IsEnabled = input.IsEnabled;
            await context.SaveChangesAsync();
            TempData["StatusMessage"] = $"تم تحديث \"{brand.Name}\".";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Delete(int id)
        {
            var brand = await context.NoonBrandUrls.FindAsync(id);
            return brand is null ? NotFound() : View(brand);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            await context.NoonBrandUrls.Where(b => b.Id == id).ExecuteDeleteAsync();
            TempData["StatusMessage"] = "تم حذف الرابط.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkUpdateEnabled([FromBody] BulkUpdateEnabledRequest request)
        {
            var count = await context.NoonBrandUrls
                .Where(b => request.Ids.Contains(b.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.IsEnabled, request.IsEnabled));

            return count == 0
                ? BadRequest(new { success = false, message = "لم يتم تحديد أي روابط." })
                : Ok(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RunNow(int id, CancellationToken cancellationToken)
        {
            if (!await scraper.RunBrandNowAsync(id, cancellationToken))
            {
                TempData["StatusMessage"] = "تعذّر التشغيل — الرابط غير موجود أو قيد التشغيل حالياً.";
                return RedirectToAction(nameof(Details), new { id });
            }

            var brand = await context.NoonBrandUrls.AsNoTracking().FirstAsync(b => b.Id == id, cancellationToken);
            TempData["StatusMessage"] = string.IsNullOrEmpty(brand.LastRunError)
                ? $"اكتمل \"{brand.Name}\": {brand.LastRunProductCount} منتج."
                : $"انتهى \"{brand.Name}\" بخطأ: {brand.LastRunError}";
            return RedirectToAction(nameof(Details), new { id });
        }

        private void Validate(NoonBrandUrl brand)
        {
            brand.Name = brand.Name?.Trim() ?? string.Empty;
            brand.Url = brand.Url?.Trim() ?? string.Empty;

            try
            {
                catalog.BuildPageUrl(brand.Url, 1);
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(nameof(NoonBrandUrl.Url), ex.Message);
            }
        }
    }
}
