using Affiliate.Data;
using Affiliate.Models;
using Affiliate.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Controllers
{
    public class ScraperSearchesController : Controller
    {
        private readonly AffiliateDbContext _context;
        private readonly IAmazonScraperService _scraperService;

        public ScraperSearchesController(
            AffiliateDbContext context,
            IAmazonScraperService scraperService)
        {
            _context = context;
            _scraperService = scraperService;
        }

        public async Task<IActionResult> Index()
        {
            var searches = await _context.ScraperSearches
                .OrderBy(s => s.Name)
                .ToListAsync();
            return View(searches);
        }

        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
                return NotFound();

            var search = await _context.ScraperSearches.FirstOrDefaultAsync(s => s.Id == id);
            if (search == null)
                return NotFound();

            return View(search);
        }

        public IActionResult Create()
        {
            return View(new ScraperSearch());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            [Bind(
                nameof(ScraperSearch.Name), nameof(ScraperSearch.Source), nameof(ScraperSearch.Domain),
                nameof(ScraperSearch.Query), nameof(ScraperSearch.Locale), nameof(ScraperSearch.StartPage),
                nameof(ScraperSearch.Parse),
                nameof(ScraperSearch.ForceHeaders), nameof(ScraperSearch.ForceCookies),
                nameof(ScraperSearch.HcPolicy), nameof(ScraperSearch.CategoryId),
                nameof(ScraperSearch.MerchantId), nameof(ScraperSearch.CheckEmptyGeo),
                nameof(ScraperSearch.SafeSearch), nameof(ScraperSearch.Currency),
                nameof(ScraperSearch.SortBy), nameof(ScraperSearch.Refinements),
                nameof(ScraperSearch.MinPrice), nameof(ScraperSearch.MaxPrice),
                nameof(ScraperSearch.GeoLocation), nameof(ScraperSearch.IntervalSeconds),
                nameof(ScraperSearch.IsEnabled))] ScraperSearch search)
        {
            if (!ModelState.IsValid)
                return View(search);

            search.CreatedAt = DateTime.UtcNow;
            _context.Add(search);
            await _context.SaveChangesAsync();
            TempData["StatusMessage"] = $"Search \"{search.Name}\" was created. The background scheduler will run it when due.";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return NotFound();

            var search = await _context.ScraperSearches.FindAsync(id);
            if (search == null)
                return NotFound();

            return View(search);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            [Bind(
                nameof(ScraperSearch.Id), nameof(ScraperSearch.Name), nameof(ScraperSearch.Source),
                nameof(ScraperSearch.Domain), nameof(ScraperSearch.Query), nameof(ScraperSearch.Locale),
                nameof(ScraperSearch.StartPage), nameof(ScraperSearch.Parse),
                nameof(ScraperSearch.ForceHeaders), nameof(ScraperSearch.ForceCookies),
                nameof(ScraperSearch.HcPolicy), nameof(ScraperSearch.CategoryId),
                nameof(ScraperSearch.MerchantId), nameof(ScraperSearch.CheckEmptyGeo),
                nameof(ScraperSearch.SafeSearch), nameof(ScraperSearch.Currency),
                nameof(ScraperSearch.SortBy), nameof(ScraperSearch.Refinements),
                nameof(ScraperSearch.MinPrice), nameof(ScraperSearch.MaxPrice),
                nameof(ScraperSearch.GeoLocation), nameof(ScraperSearch.IntervalSeconds),
                nameof(ScraperSearch.IsEnabled))] ScraperSearch search)
        {
            if (id != search.Id)
                return NotFound();

            if (!ModelState.IsValid)
                return View(search);

            var existing = await _context.ScraperSearches.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id);
            if (existing == null)
                return NotFound();

            search.CreatedAt = existing.CreatedAt;
            search.LastRunAt = existing.LastRunAt;
            search.LastRunError = existing.LastRunError;

            _context.Update(search);
            await _context.SaveChangesAsync();
            TempData["StatusMessage"] = $"Search \"{search.Name}\" was updated.";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
                return NotFound();

            var search = await _context.ScraperSearches.FirstOrDefaultAsync(s => s.Id == id);
            if (search == null)
                return NotFound();

            return View(search);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var search = await _context.ScraperSearches.FindAsync(id);
            if (search != null)
            {
                _context.ScraperSearches.Remove(search);
                await _context.SaveChangesAsync();
                TempData["StatusMessage"] = "Search was deleted.";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePrices([FromBody] List<ScraperSearchPriceUpdate> updates)
        {
            if (updates is not { Count: > 0 })
                return BadRequest(new { success = false, message = "لا توجد أسعار لحفظها." });

            var ids = updates.Select(u => u.Id).Distinct().ToList();
            var searches = await _context.ScraperSearches
                .Where(s => ids.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id);

            var errors = new List<string>();
            var saved = 0;

            foreach (var update in updates)
            {
                if (!searches.TryGetValue(update.Id, out var search))
                {
                    errors.Add($"بحث #{update.Id} غير موجود.");
                    continue;
                }

                if (update.MinPrice is < 0 || update.MaxPrice is < 0)
                {
                    errors.Add($"\"{search.Name}\": الأسعار يجب أن تكون صفر أو أكبر.");
                    continue;
                }

                if (update.MinPrice.HasValue && update.MaxPrice.HasValue && update.MinPrice > update.MaxPrice)
                {
                    errors.Add($"\"{search.Name}\": أدنى سعر لا يمكن أن يكون أكبر من أعلى سعر.");
                    continue;
                }

                search.MinPrice = update.MinPrice;
                search.MaxPrice = update.MaxPrice;
                saved++;
            }

            if (saved > 0)
                await _context.SaveChangesAsync();

            if (errors.Count > 0 && saved == 0)
                return BadRequest(new { success = false, message = string.Join(" ", errors) });

            return Json(new
            {
                success = true,
                message = errors.Count == 0
                    ? $"تم تحديث أسعار {saved} عملية بحث."
                    : $"تم تحديث {saved}؛ أخطاء: {string.Join(" ", errors)}"
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkUpdateEnabled(
            [FromBody] BulkUpdateEnabledRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Ids is not { Length: > 0 })
                return BadRequest(new { success = false, message = "لم يتم تحديد أي عمليات بحث." });

            var searches = await _context.ScraperSearches
                .Where(s => request.Ids.Contains(s.Id))
                .ToListAsync(cancellationToken);

            if (searches.Count == 0)
                return BadRequest(new { success = false, message = "لم يتم العثور على عمليات البحث المحددة." });

            foreach (var search in searches)
                search.IsEnabled = request.IsEnabled;

            await _context.SaveChangesAsync(cancellationToken);

            return Ok(new
            {
                success = true,
                message = request.IsEnabled
                    ? $"تم تفعيل {searches.Count} عملية بحث."
                    : $"تم تعطيل {searches.Count} عملية بحث.",
                searches = searches.Select(s => new { id = s.Id, isEnabled = s.IsEnabled }).ToList()
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RunNow(int id, CancellationToken cancellationToken)
        {
            var search = await _context.ScraperSearches.FindAsync(id);
            if (search == null)
                return NotFound();

            var ran = await _scraperService.RunSearchNowAsync(id, cancellationToken);
            if (!ran)
            {
                TempData["StatusMessage"] = "Could not run search — another scrape is in progress. Try again shortly.";
                return RedirectToAction(nameof(Details), new { id });
            }

            await _context.Entry(search).ReloadAsync(cancellationToken);
            if (string.IsNullOrEmpty(search.LastRunError))
                TempData["StatusMessage"] = $"\"{search.Name}\" ran successfully.";
            else
                TempData["StatusMessage"] = $"\"{search.Name}\" finished with an error: {search.LastRunError}";

            return RedirectToAction(nameof(Details), new { id });
        }
    }

    public class ScraperSearchPriceUpdate
    {
        public int Id { get; set; }
        public int? MinPrice { get; set; }
        public int? MaxPrice { get; set; }
    }

    public class BulkUpdateEnabledRequest
    {
        public int[] Ids { get; set; } = [];
        public bool IsEnabled { get; set; }
    }
}
