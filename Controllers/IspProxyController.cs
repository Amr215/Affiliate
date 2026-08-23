using Affiliate.Data;
using Affiliate.Options;
using Affiliate.Services;
using Affiliate.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Affiliate.Controllers
{
    public class IspProxyController : Controller
    {
        private readonly IIspProxyService _ispProxy;
        private readonly IspProxyOptions _options;
        private readonly AffiliateDbContext _context;

        public IspProxyController(
            IIspProxyService ispProxy,
            IOptions<IspProxyOptions> options,
            AffiliateDbContext context)
        {
            _ispProxy = ispProxy;
            _options = options.Value;
            _context = context;
        }

        public IActionResult Index()
        {
            var ports = _ispProxy.GetPortStatuses();
            var blocked = ports.Count(p => p.IsBlocked);

            return View(new IspProxyPortsIndexViewModel
            {
                Enabled = _options.Enabled,
                Host = _options.Host,
                PortMin = _options.PortMin,
                PortMax = _options.PortMax,
                ConsecutiveFailuresBeforeBlock = _options.ConsecutiveFailuresBeforeBlock,
                BlockDurationSeconds = _options.BlockDurationSeconds,
                BlockedCount = blocked,
                AvailableCount = ports.Count - blocked,
                Ports = ports
            });
        }

        /// <summary>
        /// Aggregated request counts / success rate per ISP proxy port from <see cref="Models.OxylabsRequestLog"/>.
        /// Success = StatusCode 200.
        /// </summary>
        public async Task<IActionResult> Statistics(
            [FromQuery] PortStatisticsFilterViewModel filter,
            CancellationToken cancellationToken)
        {
            var query = _context.OxylabsRequestLogs.AsNoTracking().AsQueryable();

            if (filter.From.HasValue)
                query = query.Where(l => l.RequestedAt >= filter.From.Value.Date);

            if (filter.To.HasValue)
                query = query.Where(l => l.RequestedAt < filter.To.Value.Date.AddDays(1));

            var ports = await query
                .GroupBy(l => l.Port)
                .Select(g => new PortStatisticsRow
                {
                    Port = g.Key,
                    TotalRequests = g.Count(),
                    SuccessCount = g.Count(l => l.StatusCode == 200),
                    FailedCount = g.Count(l => l.StatusCode != 200)
                })
                .ToListAsync(cancellationToken);

            ports = ports
                .OrderBy(r => r.Port == null)
                .ThenBy(r => r.Port)
                .ToList();

            return View(new PortStatisticsViewModel
            {
                Filter = filter,
                Ports = ports,
                TotalRequests = ports.Sum(p => p.TotalRequests),
                TotalSuccess = ports.Sum(p => p.SuccessCount),
                TotalFailed = ports.Sum(p => p.FailedCount)
            });
        }
    }
}
