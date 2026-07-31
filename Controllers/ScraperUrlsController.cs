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

        public async Task<IActionResult> Index()
        {
            var urls = await _context.ScraperUrls
                .OrderBy(s => s.Name)
                .ToListAsync();
            return View(urls);
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
                nameof(ScraperUrl.IsEnabled))] ScraperUrl scraperUrl)
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
                nameof(ScraperUrl.IntervalSeconds), nameof(ScraperUrl.IsEnabled))] ScraperUrl scraperUrl)
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
}
