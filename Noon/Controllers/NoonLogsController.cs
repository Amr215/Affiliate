using Affiliate.Data;
using Affiliate.Noon.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Noon.Controllers
{
    public class NoonLogsController(AffiliateDbContext context) : Controller
    {
        public async Task<IActionResult> Index([FromQuery] NoonLogFilterViewModel filter, CancellationToken ct)
        {
            filter.Page = Math.Max(1, filter.Page);
            filter.PageSize = filter.PageSize is 10 or 25 or 50 or 100 ? filter.PageSize : 25;
            filter.Proxy = string.IsNullOrWhiteSpace(filter.Proxy) ? null : filter.Proxy.Trim();

            var query = context.NoonRequestLogs.AsNoTracking();
            if (filter.BrandId.HasValue)
                query = query.Where(l => l.NoonBrandUrlId == filter.BrandId);
            if (filter.Outcome.HasValue)
                query = query.Where(l => l.Outcome == filter.Outcome);
            if (filter.StatusCode.HasValue)
                query = query.Where(l => l.StatusCode == filter.StatusCode);
            if (filter.Proxy != null)
                query = query.Where(l => l.Proxy != null && l.Proxy.Contains(filter.Proxy));
            if (filter.ErrorsOnly == true)
                query = query.Where(l => l.Outcome != NoonRequestOutcome.Success);
            if (filter.From.HasValue)
                query = query.Where(l => l.RequestedAt >= filter.From.Value.Date);
            if (filter.To.HasValue)
                query = query.Where(l => l.RequestedAt < filter.To.Value.Date.AddDays(1));

            var totalCount = await query.CountAsync(ct);
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)filter.PageSize));
            filter.Page = Math.Min(filter.Page, totalPages);

            return View(new NoonLogsIndexViewModel
            {
                Filter = filter,
                TotalCount = totalCount,
                TotalPages = totalPages,
                SuccessCount = await query.CountAsync(l => l.Outcome == NoonRequestOutcome.Success, ct),
                Logs = await query
                    .Include(l => l.NoonBrandUrl)
                    .OrderByDescending(l => l.RequestedAt)
                    .ThenByDescending(l => l.Id)
                    .Skip((filter.Page - 1) * filter.PageSize)
                    .Take(filter.PageSize)
                    .ToListAsync(ct),
                Brands = await context.NoonBrandUrls.AsNoTracking().OrderBy(b => b.Name).ToListAsync(ct)
            });
        }

        public async Task<IActionResult> Details(long id, CancellationToken ct)
        {
            var log = await context.NoonRequestLogs.AsNoTracking()
                .Include(l => l.NoonBrandUrl)
                .FirstOrDefaultAsync(l => l.Id == id, ct);
            return log is null ? NotFound() : View(log);
        }
    }
}
