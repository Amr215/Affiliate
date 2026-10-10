using Affiliate.Data;
using Affiliate.Noon.Models;
using Affiliate.Options;
using Affiliate.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Affiliate.Noon.Controllers
{
    public class NoonReportsController(
        AffiliateDbContext context,
        NoonCycleCache cycles,
        NoonProxyBlocklist blocklist,
        IIspProxyService ispProxy,
        IOptions<NoonOptions> options,
        IOptions<IspProxyOptions> proxyOptions) : Controller
    {
        private static readonly TimeSpan ReportWindow = TimeSpan.FromMinutes(10);

        public IActionResult Index() => View(new NoonCycleReportViewModel
        {
            Window = ReportWindow,
            Cycles = cycles.GetRecent(ReportWindow)
        });

        /// <summary>Noon's own block state for every configured proxy (independent of Amazon's).</summary>
        public IActionResult Proxies() => View(new NoonProxiesViewModel
        {
            Enabled = proxyOptions.Value.Enabled,
            ConsecutiveFailuresBeforeBlock = options.Value.ConsecutiveFailuresBeforeBlock,
            BlockDurationSeconds = options.Value.BlockDurationSeconds,
            Proxies = ispProxy.GetPortStatuses()
                .Select(p => $"{p.Host}:{p.Port}")
                .Select(key =>
                {
                    var (failures, blockedUntil) = blocklist.GetState(key);
                    return new NoonProxyStatus(key, failures, blockedUntil);
                })
                .ToList()
        });

        /// <summary>Request counts and success rate per proxy from <see cref="NoonRequestLog"/>.</summary>
        public async Task<IActionResult> Statistics(DateTime? from, DateTime? to, CancellationToken ct)
        {
            var query = context.NoonRequestLogs.AsNoTracking();
            if (from.HasValue)
                query = query.Where(l => l.RequestedAt >= from.Value.Date);
            if (to.HasValue)
                query = query.Where(l => l.RequestedAt < to.Value.Date.AddDays(1));

            var rows = await query
                .GroupBy(l => l.Proxy)
                .Select(g => new NoonProxyStatisticsRow
                {
                    Proxy = g.Key,
                    TotalRequests = g.Count(),
                    SuccessCount = g.Count(l => l.Outcome == NoonRequestOutcome.Success),
                    HttpErrorCount = g.Count(l => l.Outcome == NoonRequestOutcome.HttpError),
                    TimeoutCount = g.Count(l => l.Outcome == NoonRequestOutcome.Timeout),
                    TransportCount = g.Count(l => l.Outcome == NoonRequestOutcome.Transport),
                    InvalidResponseCount = g.Count(l => l.Outcome == NoonRequestOutcome.InvalidResponse),
                    AvgDurationMs = g.Average(l => (double)l.DurationMs)
                })
                .ToListAsync(ct);

            return View(new NoonProxyStatisticsViewModel
            {
                From = from,
                To = to,
                Proxies = rows
                    .OrderBy(r => r.SuccessRate)
                    .ThenByDescending(r => r.FailedCount)
                    .ThenBy(r => r.Proxy)
                    .ToList()
            });
        }
    }
}
