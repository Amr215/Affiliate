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
            var blocked = ports.Count(p => p.Amazon.IsBlocked);

            return View(new IspProxyPortsIndexViewModel
            {
                Enabled = _options.Enabled,
                ProxyCount = ports.Count,
                ConsecutiveFailuresBeforeBlock = _options.ConsecutiveFailuresBeforeBlock,
                BlockDurationSeconds = _options.BlockDurationSeconds,
                BlockedCount = blocked,
                AvailableCount = ports.Count - blocked,
                Ports = ports
            });
        }

        /// <summary>Proxies Google Translate rejected too often; they stay off that route until the block expires.</summary>
        public IActionResult GoogleBlocked()
        {
            var ports = _ispProxy.GetPortStatuses();

            return View(new IspProxyGoogleBlockedViewModel
            {
                ProxyCount = ports.Count,
                TranslateFailuresBeforeBlock = _options.TranslateFailuresBeforeBlock,
                TranslateBlockDurationSeconds = _options.TranslateBlockDurationSeconds,
                Ports = ports
                    .Where(p => p.Translate.IsBlocked)
                    .OrderByDescending(p => p.Translate.BlockedUntilUtc)
                    .ToList()
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
                    FailedCount = g.Count(l => l.StatusCode != 200),
                    GoogleSuccessCount = g.Count(l => l.StatusCode == 200
                        && l.RequestBody != null
                        && (l.RequestBody.Contains(GoogleTranslateProxy.LogMarker)
                            || l.RequestBody.Contains(GoogleTranslateProxy.HostSuffix))),
                    GoogleFailedCount = g.Count(l => l.StatusCode != 200
                        && l.RequestBody != null
                        && (l.RequestBody.Contains(GoogleTranslateProxy.LogMarker)
                            || l.RequestBody.Contains(GoogleTranslateProxy.HostSuffix))),
                    AmazonSuccessCount = g.Count(l => l.StatusCode == 200
                        && (l.RequestBody == null
                            || (!l.RequestBody.Contains(GoogleTranslateProxy.LogMarker)
                                && !l.RequestBody.Contains(GoogleTranslateProxy.HostSuffix)))),
                    AmazonFailedCount = g.Count(l => l.StatusCode != 200
                        && (l.RequestBody == null
                            || (!l.RequestBody.Contains(GoogleTranslateProxy.LogMarker)
                                && !l.RequestBody.Contains(GoogleTranslateProxy.HostSuffix))))
                })
                .ToListAsync(cancellationToken);

            ports = ports
                .OrderBy(r => r.Port == null)
                .ThenBy(r => r.SuccessRate)
                .ThenByDescending(r => r.FailedCount)
                .ThenBy(r => r.Port)
                .ToList();

            return View(new PortStatisticsViewModel
            {
                Filter = filter,
                Ports = ports,
                TotalRequests = ports.Sum(p => p.TotalRequests),
                TotalSuccess = ports.Sum(p => p.SuccessCount),
                TotalFailed = ports.Sum(p => p.FailedCount),
                TotalGoogleSuccess = ports.Sum(p => p.GoogleSuccessCount),
                TotalGoogleFailed = ports.Sum(p => p.GoogleFailedCount),
                TotalAmazonSuccess = ports.Sum(p => p.AmazonSuccessCount),
                TotalAmazonFailed = ports.Sum(p => p.AmazonFailedCount)
            });
        }

        /// <summary>
        /// Aggregates Amazon success rates by proxy IP /24 range.
        /// Prefers <see cref="Models.OxylabsRequestLog.ProxyHost"/> from each log; older rows
        /// without a host fall back to the configured proxy list via port (ambiguous ports skipped).
        /// </summary>
        public async Task<IActionResult> IpRanges(
            [FromQuery] IpRangeStatisticsFilterViewModel filter,
            CancellationToken cancellationToken)
        {
            if (filter.MinAmazonRequests < 0)
                filter.MinAmazonRequests = 0;

            var query = _context.OxylabsRequestLogs.AsNoTracking().AsQueryable();

            if (filter.From.HasValue)
                query = query.Where(l => l.RequestedAt >= filter.From.Value.Date);

            if (filter.To.HasValue)
                query = query.Where(l => l.RequestedAt < filter.To.Value.Date.AddDays(1));

            // Same Amazon-vs-Google split as Statistics. Group by logged host+port so new rows
            // keep an exact IP even when the same port exists on multiple proxies.
            var hostPortStats = await query
                .Where(l => l.Port != null || (l.ProxyHost != null && l.ProxyHost != ""))
                .GroupBy(l => new { l.ProxyHost, l.Port })
                .Select(g => new
                {
                    g.Key.ProxyHost,
                    g.Key.Port,
                    AmazonSuccessCount = g.Count(l => l.StatusCode == 200
                        && (l.RequestBody == null
                            || (!l.RequestBody.Contains(GoogleTranslateProxy.LogMarker)
                                && !l.RequestBody.Contains(GoogleTranslateProxy.HostSuffix)))),
                    AmazonFailedCount = g.Count(l => l.StatusCode != 200
                        && (l.RequestBody == null
                            || (!l.RequestBody.Contains(GoogleTranslateProxy.LogMarker)
                                && !l.RequestBody.Contains(GoogleTranslateProxy.HostSuffix))))
                })
                .ToListAsync(cancellationToken);

            var hostsByPort = _ispProxy.GetPortStatuses()
                .Where(p => !string.IsNullOrWhiteSpace(p.Host))
                .GroupBy(p => p.Port)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(p => p.Host!).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

            var proxiesPerRange = hostsByPort.Values
                .SelectMany(hosts => hosts)
                .Select(ToCidr24)
                .Where(r => r != null)
                .GroupBy(r => r!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var rangeBuckets = new Dictionary<string, IpRangeStatisticsRow>(StringComparer.Ordinal);
            var ambiguous = new IpRangeStatisticsRow
            {
                IpRange = null,
                IsAmbiguous = true
            };
            var unmappedPorts = 0;
            var portsPerRange = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

            foreach (var row in hostPortStats)
            {
                if (row.AmazonSuccessCount + row.AmazonFailedCount == 0)
                    continue;

                string? host = null;
                if (!string.IsNullOrWhiteSpace(row.ProxyHost))
                {
                    host = row.ProxyHost;
                }
                else if (row.Port is int port
                         && hostsByPort.TryGetValue(port, out var hosts)
                         && hosts.Count == 1)
                {
                    host = hosts[0];
                }
                else
                {
                    if (row.Port is null
                        || !hostsByPort.TryGetValue(row.Port.Value, out var mapped)
                        || mapped.Count == 0)
                        unmappedPorts++;

                    ambiguous.PortCount++;
                    ambiguous.AmazonSuccessCount += row.AmazonSuccessCount;
                    ambiguous.AmazonFailedCount += row.AmazonFailedCount;
                    continue;
                }

                var range = ToCidr24(host);
                if (range is null)
                {
                    unmappedPorts++;
                    ambiguous.PortCount++;
                    ambiguous.AmazonSuccessCount += row.AmazonSuccessCount;
                    ambiguous.AmazonFailedCount += row.AmazonFailedCount;
                    continue;
                }

                if (!rangeBuckets.TryGetValue(range, out var bucket))
                {
                    bucket = new IpRangeStatisticsRow
                    {
                        IpRange = range,
                        ProxyCount = proxiesPerRange.GetValueOrDefault(range)
                    };
                    rangeBuckets[range] = bucket;
                    portsPerRange[range] = new HashSet<int>();
                }

                if (row.Port is int attributedPort)
                    portsPerRange[range].Add(attributedPort);

                bucket.AmazonSuccessCount += row.AmazonSuccessCount;
                bucket.AmazonFailedCount += row.AmazonFailedCount;
            }

            foreach (var (range, ports) in portsPerRange)
                rangeBuckets[range].PortCount = ports.Count;

            var ranges = rangeBuckets.Values
                .Where(r => r.AmazonTotal >= filter.MinAmazonRequests)
                .OrderByDescending(r => r.AmazonSuccessRate)
                .ThenByDescending(r => r.AmazonTotal)
                .ThenBy(r => r.IpRange)
                .ToList();

            if (ambiguous.AmazonTotal > 0)
                ranges.Add(ambiguous);

            return View(new IpRangeStatisticsViewModel
            {
                Filter = filter,
                Ranges = ranges,
                TotalAmazonSuccess = rangeBuckets.Values.Sum(r => r.AmazonSuccessCount)
                    + ambiguous.AmazonSuccessCount,
                TotalAmazonFailed = rangeBuckets.Values.Sum(r => r.AmazonFailedCount)
                    + ambiguous.AmazonFailedCount,
                AmbiguousPortCount = ambiguous.PortCount,
                UnmappedPortCount = unmappedPorts
            });
        }

        /// <summary>First three IPv4 octets as a /24 CIDR label, or null if not a dotted IPv4.</summary>
        private static string? ToCidr24(string? host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return null;

            var parts = host.Split('.');
            if (parts.Length != 4)
                return null;

            for (var i = 0; i < 4; i++)
            {
                if (!byte.TryParse(parts[i], out _))
                    return null;
            }

            return $"{parts[0]}.{parts[1]}.{parts[2]}.0/24";
        }
    }
}
