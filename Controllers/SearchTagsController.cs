using Affiliate.Data;
using Affiliate.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Controllers
{
    public class SearchTagsController(AffiliateDbContext context) : Controller
    {
        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var tags = await context.SearchTags.AsNoTracking()
                .OrderBy(t => t.NameAr)
                .Select(t => new SearchTagListItem
                {
                    Id = t.Id,
                    NameAr = t.NameAr,
                    NameEn = t.NameEn,
                    ScraperUrlCount = t.ScraperUrls.Count
                })
                .ToListAsync(cancellationToken);

            return View(tags);
        }

        public IActionResult Create() => View(new SearchTag());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            [Bind(nameof(SearchTag.NameAr), nameof(SearchTag.NameEn))] SearchTag tag,
            CancellationToken cancellationToken)
        {
            Normalize(tag);
            if (!ModelState.IsValid)
                return View(tag);

            context.SearchTags.Add(tag);
            await context.SaveChangesAsync(cancellationToken);
            TempData["StatusMessage"] = $"تم إنشاء الوسم \"{tag.NameAr}\".";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(int? id, CancellationToken cancellationToken)
        {
            if (id == null)
                return NotFound();

            var tag = await context.SearchTags.FindAsync([id.Value], cancellationToken);
            return tag == null ? NotFound() : View(tag);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            [Bind(nameof(SearchTag.Id), nameof(SearchTag.NameAr), nameof(SearchTag.NameEn))] SearchTag tag,
            CancellationToken cancellationToken)
        {
            if (id != tag.Id)
                return NotFound();

            Normalize(tag);
            if (!ModelState.IsValid)
                return View(tag);

            if (!await context.SearchTags.AnyAsync(t => t.Id == id, cancellationToken))
                return NotFound();

            context.Update(tag);
            await context.SaveChangesAsync(cancellationToken);
            TempData["StatusMessage"] = $"تم تحديث الوسم \"{tag.NameAr}\".";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Delete(int? id, CancellationToken cancellationToken)
        {
            if (id == null)
                return NotFound();

            var tag = await context.SearchTags.AsNoTracking()
                .Select(t => new SearchTagListItem
                {
                    Id = t.Id,
                    NameAr = t.NameAr,
                    NameEn = t.NameEn,
                    ScraperUrlCount = t.ScraperUrls.Count
                })
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

            return tag == null ? NotFound() : View(tag);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id, CancellationToken cancellationToken)
        {
            var tag = await context.SearchTags.FindAsync([id], cancellationToken);
            if (tag != null)
            {
                context.SearchTags.Remove(tag);
                await context.SaveChangesAsync(cancellationToken);
                TempData["StatusMessage"] = $"تم حذف الوسم \"{tag.NameAr}\".";
            }

            return RedirectToAction(nameof(Index));
        }

        private static void Normalize(SearchTag tag)
        {
            tag.NameAr = (tag.NameAr ?? string.Empty).Trim();
            tag.NameEn = (tag.NameEn ?? string.Empty).Trim();
        }
    }

    public class SearchTagListItem
    {
        public int Id { get; set; }
        public string NameAr { get; set; } = string.Empty;
        public string NameEn { get; set; } = string.Empty;
        public int ScraperUrlCount { get; set; }
    }
}
