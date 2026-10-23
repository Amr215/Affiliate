using Affiliate.Data;
using Affiliate.Models;
using Affiliate.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Controllers
{
    public class ScraperUrlsController : Controller
    {
        private readonly AffiliateDbContext _context;
        private readonly IAmazonScraperService _scraperService;

        public ScraperUrlsController(
            AffiliateDbContext context,
            IAmazonScraperService scraperService)
        {
            _context = context;
            _scraperService = scraperService;
        }

        public async Task<IActionResult> Index(
            string? search, string? category, bool? isEnabled, bool? isAsinRecheckEnabled, string? sort)
        {
            var query = _context.ScraperUrls.AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(s => s.Name.Contains(search.Trim()));
            if (string.Equals(category, "none", StringComparison.OrdinalIgnoreCase))
                query = query.Where(s => s.Category == null);
            else if (Enum.TryParse<ScraperCategory>(category, ignoreCase: true, out var cat) && Enum.IsDefined(cat))
                query = query.Where(s => s.Category == cat);
            if (isEnabled.HasValue)
                query = query.Where(s => s.IsEnabled == isEnabled.Value);
            if (isAsinRecheckEnabled.HasValue)
                query = query.Where(s => s.IsAsinRecheckEnabled == isAsinRecheckEnabled.Value);

            query = sort switch
            {
                "name_desc" => query.OrderByDescending(s => s.Name),
                "lastRun" => query.OrderByDescending(s => s.LastRunAt),
                "interval" => query.OrderBy(s => s.IntervalSeconds),
                "created" => query.OrderByDescending(s => s.CreatedAt),
                _ => query.OrderBy(s => s.Name)
            };

            ViewData["Search"] = search;
            ViewData["Category"] = category;
            ViewData["IsEnabled"] = isEnabled;
            ViewData["IsAsinRecheckEnabled"] = isAsinRecheckEnabled;
            ViewData["Sort"] = sort;
            ViewData["AllNames"] = await _context.ScraperUrls
                .OrderBy(s => s.Name)
                .Select(s => s.Name)
                .ToListAsync();
            ViewData["ProductCounts"] = await _context.Products.AsNoTracking()
                .Where(p => p.ScraperUrlId != null)
                .GroupBy(p => p.ScraperUrlId!.Value)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.Count);

            // Dictionary cannot use null keys, so uncategorized is kept separate.
            var categoryRows = await _context.Products.AsNoTracking()
                .Select(p => p.ScraperUrl != null ? p.ScraperUrl.Category : null)
                .GroupBy(c => c)
                .Select(g => new { Category = g.Key, Count = g.Count() })
                .ToListAsync();
            ViewData["CategoryProductCounts"] = categoryRows
                .Where(r => r.Category != null)
                .ToDictionary(r => r.Category!.Value, r => r.Count);
            ViewData["UncategorizedProductCount"] = categoryRows
                .Where(r => r.Category == null)
                .Select(r => r.Count)
                .FirstOrDefault();

            return View(await query.ToListAsync());
        }

        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
                return NotFound();

            var item = await _context.ScraperUrls.FirstOrDefaultAsync(s => s.Id == id);
            if (item == null)
                return NotFound();

            return View(item);
        }

        public IActionResult Create()
        {
            return View(new ScraperUrl());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            [Bind(
                nameof(ScraperUrl.Name), nameof(ScraperUrl.Url), nameof(ScraperUrl.Domain),
                nameof(ScraperUrl.StartPage), nameof(ScraperUrl.IntervalSeconds),
                nameof(ScraperUrl.IsEnabled), nameof(ScraperUrl.IsAsinRecheckEnabled),
                nameof(ScraperUrl.LongUnavailableRecheckIntervalMinutes),
                nameof(ScraperUrl.Category), nameof(ScraperUrl.DropAlertPercent))] ScraperUrl scraperUrl)
        {
            Normalize(scraperUrl);
            if (!ModelState.IsValid)
                return View(scraperUrl);

            scraperUrl.CreatedAt = DateTime.UtcNow;
            _context.Add(scraperUrl);
            await _context.SaveChangesAsync();
            TempData["StatusMessage"] = $"URL \"{scraperUrl.Name}\" was created. The background scheduler will run it when due.";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return NotFound();

            var item = await _context.ScraperUrls.FindAsync(id);
            if (item == null)
                return NotFound();

            return View(item);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            [Bind(
                nameof(ScraperUrl.Id), nameof(ScraperUrl.Name), nameof(ScraperUrl.Url),
                nameof(ScraperUrl.Domain), nameof(ScraperUrl.StartPage),
                nameof(ScraperUrl.IntervalSeconds), nameof(ScraperUrl.IsEnabled),
                nameof(ScraperUrl.IsAsinRecheckEnabled),
                nameof(ScraperUrl.LongUnavailableRecheckIntervalMinutes),
                nameof(ScraperUrl.Category),
                nameof(ScraperUrl.DropAlertPercent))] ScraperUrl scraperUrl)
        {
            if (id != scraperUrl.Id)
                return NotFound();

            Normalize(scraperUrl);
            if (!ModelState.IsValid)
                return View(scraperUrl);

            var existing = await _context.ScraperUrls.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id);
            if (existing == null)
                return NotFound();

            scraperUrl.CreatedAt = existing.CreatedAt;
            scraperUrl.LastRunAt = existing.LastRunAt;
            scraperUrl.LastRunError = existing.LastRunError;

            _context.Update(scraperUrl);
            await _context.SaveChangesAsync();
            TempData["StatusMessage"] = $"URL \"{scraperUrl.Name}\" was updated.";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
                return NotFound();

            var item = await _context.ScraperUrls.FirstOrDefaultAsync(s => s.Id == id);
            if (item == null)
                return NotFound();

            return View(item);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var item = await _context.ScraperUrls.FindAsync(id);
            if (item != null)
            {
                _context.ScraperUrls.Remove(item);
                await _context.SaveChangesAsync();
                TempData["StatusMessage"] = "URL scrape was deleted.";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkUpdateEnabled(
            [FromBody] BulkUpdateEnabledRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Ids is not { Length: > 0 })
                return BadRequest(new { success = false, message = "لم يتم تحديد أي روابط." });

            var items = await _context.ScraperUrls
                .Where(s => request.Ids.Contains(s.Id))
                .ToListAsync(cancellationToken);

            if (items.Count == 0)
                return BadRequest(new { success = false, message = "لم يتم العثور على الروابط المحددة." });

            foreach (var item in items)
                item.IsEnabled = request.IsEnabled;

            await _context.SaveChangesAsync(cancellationToken);

            return Ok(new
            {
                success = true,
                message = request.IsEnabled
                    ? $"تم تفعيل {items.Count} رابط."
                    : $"تم تعطيل {items.Count} رابط.",
                searches = items.Select(s => new { id = s.Id, isEnabled = s.IsEnabled }).ToList()
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkUpdateAsinRecheck(
            [FromBody] BulkUpdateAsinRecheckRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Ids is not { Length: > 0 })
                return BadRequest(new { success = false, message = "لم يتم تحديد أي روابط." });

            var updated = await _context.ScraperUrls
                .Where(s => request.Ids.Contains(s.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.IsAsinRecheckEnabled, request.IsAsinRecheckEnabled),
                    cancellationToken);

            if (updated == 0)
                return BadRequest(new { success = false, message = "لم يتم العثور على الروابط المحددة." });

            return Ok(new
            {
                success = true,
                message = request.IsAsinRecheckEnabled
                    ? $"تم تفعيل فحص ASIN لـ {updated} رابط."
                    : $"تم تعطيل فحص ASIN لـ {updated} رابط."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkAssignCategory(
            [FromBody] BulkAssignCategoryRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Ids is not { Length: > 0 })
                return BadRequest(new { success = false, message = "لم يتم تحديد أي روابط." });

            if (request.Category is { } category && !Enum.IsDefined(category))
                return BadRequest(new { success = false, message = "تصنيف غير معروف." });

            var items = await _context.ScraperUrls
                .Where(s => request.Ids.Contains(s.Id))
                .ToListAsync(cancellationToken);

            if (items.Count == 0)
                return BadRequest(new { success = false, message = "لم يتم العثور على الروابط المحددة." });

            foreach (var item in items)
                item.Category = request.Category;

            await _context.SaveChangesAsync(cancellationToken);

            return Ok(new
            {
                success = true,
                message = request.Category is { } assigned
                    ? $"تم تعيين تصنيف \"{assigned.DisplayName()}\" لـ {items.Count} رابط."
                    : $"تم إزالة التصنيف من {items.Count} رابط."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDropAlertPercents(
            [FromBody] SaveDropAlertPercentsRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Items is not { Length: > 0 })
                return BadRequest(new { success = false, message = "لا توجد تغييرات للحفظ." });

            if (request.Items.Any(i => i.DropAlertPercent is < 1 or > 100))
                return BadRequest(new { success = false, message = "حد التنبيه يجب أن يكون بين 1 و 100." });

            var byId = request.Items.ToDictionary(i => i.Id, i => i.DropAlertPercent);
            var items = await _context.ScraperUrls
                .Where(s => byId.Keys.Contains(s.Id))
                .ToListAsync(cancellationToken);

            if (items.Count == 0)
                return BadRequest(new { success = false, message = "لم يتم العثور على الروابط." });

            foreach (var item in items)
                item.DropAlertPercent = byId[item.Id];

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(new { success = true, message = $"تم حفظ حد التنبيه لـ {items.Count} رابط." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveLongUnavailableRecheckInterval(
            [FromBody] SaveLongUnavailableRecheckIntervalRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Minutes is < 1 or > 10080)
                return BadRequest(new { success = false, message = "الفاصل يجب أن يكون بين 1 و 10080 دقيقة." });

            var updated = await _context.ScraperUrls
                .Where(s => s.Id == request.Id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.LongUnavailableRecheckIntervalMinutes, request.Minutes),
                    cancellationToken);

            if (updated == 0)
                return BadRequest(new { success = false, message = "لم يتم العثور على الرابط." });

            return Ok(new { success = true, minutes = request.Minutes });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RunNow(int id, CancellationToken cancellationToken)
        {
            var item = await _context.ScraperUrls.FindAsync(id);
            if (item == null)
                return NotFound();

            var ran = await _scraperService.RunSearchNowAsync(id, cancellationToken);
            if (!ran)
            {
                TempData["StatusMessage"] = "Could not run scrape — another scrape is in progress. Try again shortly.";
                return RedirectToAction(nameof(Details), new { id });
            }

            await _context.Entry(item).ReloadAsync(cancellationToken);
            if (string.IsNullOrEmpty(item.LastRunError))
                TempData["StatusMessage"] = $"\"{item.Name}\" ran successfully.";
            else
                TempData["StatusMessage"] = $"\"{item.Name}\" finished with an error: {item.LastRunError}";

            return RedirectToAction(nameof(Details), new { id });
        }

        private static void Normalize(ScraperUrl scraperUrl)
        {
            scraperUrl.Name = scraperUrl.Name?.Trim() ?? string.Empty;
            scraperUrl.Url = scraperUrl.Url?.Trim() ?? string.Empty;
            scraperUrl.Domain = string.IsNullOrWhiteSpace(scraperUrl.Domain)
                ? InferDomain(scraperUrl.Url)
                : scraperUrl.Domain.Trim().Trim('.');
            if (scraperUrl.StartPage < 1)
                scraperUrl.StartPage = 1;
        }

        private static string InferDomain(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return "eg";

            var host = uri.Host; // www.amazon.eg
            var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[^1] : "eg";
        }
    }

    public class BulkUpdateEnabledRequest
    {
        public int[] Ids { get; set; } = [];
        public bool IsEnabled { get; set; }
    }

    public class BulkUpdateAsinRecheckRequest
    {
        public int[] Ids { get; set; } = [];
        public bool IsAsinRecheckEnabled { get; set; }
    }

    public class BulkAssignCategoryRequest
    {
        public int[] Ids { get; set; } = [];

        /// <summary>Null clears the category, which stops Telegram alerts for these URLs.</summary>
        public ScraperCategory? Category { get; set; }
    }

    public class SaveDropAlertPercentsRequest
    {
        public DropAlertPercentItem[] Items { get; set; } = [];
    }

    public class DropAlertPercentItem
    {
        public int Id { get; set; }
        public decimal DropAlertPercent { get; set; }
    }

    public class SaveLongUnavailableRecheckIntervalRequest
    {
        public int Id { get; set; }
        public int Minutes { get; set; }
    }
}
