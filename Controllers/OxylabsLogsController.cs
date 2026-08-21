using Affiliate.Data;
using Affiliate.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Controllers
{
    public class OxylabsLogsController : Controller
    {
        private readonly AffiliateDbContext _context;

        public OxylabsLogsController(AffiliateDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index(
            [FromQuery] OxylabsLogFilterViewModel filter,
            CancellationToken cancellationToken)
        {
            NormalizeFilter(filter);

            var query = _context.OxylabsRequestLogs.AsNoTracking().AsQueryable();

            if (filter.ScraperUrlId.HasValue)
                query = query.Where(l => l.ScraperUrlId == filter.ScraperUrlId.Value);

            if (filter.StatusCode.HasValue)
                query = query.Where(l => l.StatusCode == filter.StatusCode.Value);

            if (filter.Port.HasValue)
                query = query.Where(l => l.Port == filter.Port.Value);

            if (filter.ErrorsOnly == true)
                query = query.Where(l => l.StatusCode != 200);

            if (filter.From.HasValue)
                query = query.Where(l => l.RequestedAt >= filter.From.Value.Date);

            if (filter.To.HasValue)
                query = query.Where(l => l.RequestedAt < filter.To.Value.Date.AddDays(1));

            var totalCount = await query.CountAsync(cancellationToken);
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)filter.PageSize));
            if (filter.Page > totalPages)
                filter.Page = totalPages;

            var logs = await query
                .OrderByDescending(l => l.RequestedAt)
                .ThenByDescending(l => l.Id)
                .Skip((filter.Page - 1) * filter.PageSize)
                .Take(filter.PageSize)
                .Select(l => new OxylabsRequestLogListItem
                {
                    Id = l.Id,
                    ScraperUrlId = l.ScraperUrlId,
                    SearchName = l.ScraperUrl != null
                        ? l.ScraperUrl.Name
                        : (l.ScraperUrlId == null ? "ASIN recheck" : ("#" + l.ScraperUrlId)),
                    Page = l.Page,
                    RequestedAt = l.RequestedAt,
                    StatusCode = l.StatusCode,
                    StatusPhrase = l.StatusPhrase,
                    Port = l.Port,
                    HasResponseBody = l.ResponseBody != null && l.ResponseBody != ""
                })
                .ToListAsync(cancellationToken);

            var searches = await _context.ScraperUrls.AsNoTracking()
                .OrderBy(s => s.Name)
                .Select(s => new ScraperUrlOption { Id = s.Id, Name = s.Name, Url = s.Url })
                .ToListAsync(cancellationToken);

            return View(new OxylabsLogsIndexViewModel
            {
                Filter = filter,
                Logs = logs,
                TotalCount = totalCount,
                TotalPages = totalPages,
                Searches = searches
            });
        }

        public async Task<IActionResult> Details(long? id, CancellationToken cancellationToken)
        {
            if (id == null)
                return NotFound();

            var log = await _context.OxylabsRequestLogs.AsNoTracking()
                .Include(l => l.ScraperUrl)
                .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

            if (log == null)
                return NotFound();

            return View(log);
        }

        private static void NormalizeFilter(OxylabsLogFilterViewModel filter)
        {
            filter.Page = Math.Max(1, filter.Page);
            filter.PageSize = filter.PageSize switch
            {
                10 or 25 or 50 or 100 => filter.PageSize,
                _ => 25
            };
        }
    }
}
