using Affiliate.Data;
using Affiliate.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Controllers
{
    public class SearchTagsController(AffiliateDbContext context) : Controller
    {
        public async Task<IActionResult> Index(string? category, CancellationToken cancellationToken)
        {
            var query = context.SearchTags.AsNoTracking().AsQueryable();
            if (Enum.TryParse<SearchTagCategory>(category, ignoreCase: true, out var cat) && Enum.IsDefined(cat))
                query = query.Where(t => t.Category == cat);

            var tags = await query
                .OrderBy(t => t.Category)
                .ThenBy(t => t.Position)
                .ThenBy(t => t.NameAr)
                .Select(t => new SearchTagListItem
                {
                    Id = t.Id,
                    NameAr = t.NameAr,
                    NameEn = t.NameEn,
                    Category = t.Category,
                    Position = t.Position,
                    ShowInDeals = t.ShowInDeals,
                    ScraperUrlCount = t.ScraperUrls.Count
                })
                .ToListAsync(cancellationToken);

            ViewData["Category"] = category;
            return View(tags);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SavePosition(
            [FromBody] SaveSearchTagPositionRequest request,
            CancellationToken cancellationToken)
        {
            var updated = await context.SearchTags
                .Where(t => t.Id == request.Id)
                .ExecuteUpdateAsync(
                    t => t.SetProperty(x => x.Position, request.Position),
                    cancellationToken);

            if (updated == 0)
                return BadRequest(new { success = false, message = "لم يتم العثور على الوسم." });

            return Ok(new { success = true, position = request.Position });
        }

        public IActionResult Create() => View(new SearchTag());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            [Bind(nameof(SearchTag.NameAr), nameof(SearchTag.NameEn), nameof(SearchTag.Category), nameof(SearchTag.Position), nameof(SearchTag.ShowInDeals))] SearchTag tag,
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
            [Bind(nameof(SearchTag.Id), nameof(SearchTag.NameAr), nameof(SearchTag.NameEn), nameof(SearchTag.Category), nameof(SearchTag.Position), nameof(SearchTag.ShowInDeals))] SearchTag tag,
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
                    Category = t.Category,
                    Position = t.Position,
                    ShowInDeals = t.ShowInDeals,
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
        public SearchTagCategory Category { get; set; }
        public int Position { get; set; }
        public bool ShowInDeals { get; set; } = true;
        public int ScraperUrlCount { get; set; }
    }

    public class SaveSearchTagPositionRequest
    {
        public int Id { get; set; }
        public int Position { get; set; }
    }
}
