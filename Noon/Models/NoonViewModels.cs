using System.ComponentModel.DataAnnotations;

namespace Affiliate.Noon.Models
{
    public class NoonProductFilterViewModel
    {
        public string? Search { get; set; }
        public string? Sku { get; set; }
        public int? BrandId { get; set; }
        public string? StoreName { get; set; }
        public bool? IsAvailable { get; set; }
        public bool? IsBlocked { get; set; }
        public bool HasNotAvailableDate { get; set; }
        public bool? IsBestSeller { get; set; }
        public int? MinPrice { get; set; }
        public int? MaxPrice { get; set; }
        public decimal? MinDropPercent { get; set; }
        public double? MinRating { get; set; }
        public int? MinReviews { get; set; }

        [DataType(DataType.Date)]
        public DateTime? LastCheckedFrom { get; set; }

        [DataType(DataType.Date)]
        public DateTime? LastCheckedTo { get; set; }

        [DataType(DataType.Date)]
        public DateTime? CreatedFrom { get; set; }

        [DataType(DataType.Date)]
        public DateTime? CreatedTo { get; set; }

        public string SortBy { get; set; } = "DropPercent";
        public string SortDir { get; set; } = "desc";
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 100;
    }

    public class NoonProductsIndexViewModel
    {
        public NoonProductFilterViewModel Filter { get; set; } = new();
        public IReadOnlyList<NoonProduct> Products { get; set; } = [];
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
        public IReadOnlyList<NoonBrandUrl> Brands { get; set; } = [];
        public IReadOnlyList<string> Stores { get; set; } = [];
    }

    public class NoonProductDetailsViewModel
    {
        public NoonProduct Product { get; set; } = null!;
        public IReadOnlyList<NoonPriceHistory> History { get; set; } = [];

        [DataType(DataType.Date)]
        public DateTime? HistoryFrom { get; set; }

        [DataType(DataType.Date)]
        public DateTime? HistoryTo { get; set; }
    }

    public class NoonLogFilterViewModel
    {
        public int? BrandId { get; set; }
        public NoonRequestOutcome? Outcome { get; set; }
        public int? StatusCode { get; set; }
        public string? Proxy { get; set; }

        /// <summary>When true, only failed attempts.</summary>
        public bool? ErrorsOnly { get; set; }

        [DataType(DataType.Date)]
        public DateTime? From { get; set; }

        [DataType(DataType.Date)]
        public DateTime? To { get; set; }

        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 25;
    }

    public class NoonLogsIndexViewModel
    {
        public NoonLogFilterViewModel Filter { get; set; } = new();
        public IReadOnlyList<NoonRequestLog> Logs { get; set; } = [];
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
        public IReadOnlyList<NoonBrandUrl> Brands { get; set; } = [];

        /// <summary>Successful requests across the whole filter.</summary>
        public int SuccessCount { get; set; }

        public int SuccessOnPageCount => Logs.Count(l => l.Outcome == NoonRequestOutcome.Success);
    }

    public class NoonCycleReportViewModel
    {
        public TimeSpan Window { get; set; }
        public IReadOnlyList<NoonCycleSnapshot> Cycles { get; set; } = [];

        public int CycleCount => Cycles.Count;
        public int TotalSuccessPages => Cycles.Sum(c => c.SuccessPages);
        public int TotalFailedPages => Cycles.Sum(c => c.FailedPages);
        public int TotalPage1Success => Cycles.Sum(c => c.Page1Success);
        public int TotalCompleteBrands => Cycles.Sum(c => c.CompleteBrands);
        public int TotalIncompleteBrands => Cycles.Sum(c => c.IncompleteBrands);
        public int TotalProducts => Cycles.Sum(c => c.Products);
        public double AvgDurationSeconds => Cycles.Count == 0 ? 0 : Cycles.Average(c => c.DurationSeconds);

        public double PageSuccessRate
        {
            get
            {
                var total = TotalSuccessPages + TotalFailedPages;
                return total == 0 ? 0 : 100.0 * TotalSuccessPages / total;
            }
        }
    }

    public record NoonProxyStatus(string Key, int ConsecutiveFailures, DateTime? BlockedUntilUtc)
    {
        public bool IsBlocked => BlockedUntilUtc.HasValue;

        public int? RemainingBlockSeconds =>
            BlockedUntilUtc is { } until ? Math.Max(0, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds)) : null;
    }

    public class NoonProxiesViewModel
    {
        public bool Enabled { get; set; }
        public int ConsecutiveFailuresBeforeBlock { get; set; }
        public int BlockDurationSeconds { get; set; }
        public IReadOnlyList<NoonProxyStatus> Proxies { get; set; } = [];

        public int BlockedCount => Proxies.Count(p => p.IsBlocked);
        public int AvailableCount => Proxies.Count - BlockedCount;
    }

    public class NoonProxyStatisticsRow
    {
        public string? Proxy { get; set; }
        public int TotalRequests { get; set; }
        public int SuccessCount { get; set; }
        public int HttpErrorCount { get; set; }
        public int TimeoutCount { get; set; }
        public int TransportCount { get; set; }
        public int InvalidResponseCount { get; set; }
        public double AvgDurationMs { get; set; }

        public int FailedCount => TotalRequests - SuccessCount;
        public double SuccessRate => TotalRequests == 0 ? 0 : 100.0 * SuccessCount / TotalRequests;
    }

    public class NoonProxyStatisticsViewModel
    {
        [DataType(DataType.Date)]
        public DateTime? From { get; set; }

        [DataType(DataType.Date)]
        public DateTime? To { get; set; }

        public IReadOnlyList<NoonProxyStatisticsRow> Proxies { get; set; } = [];

        public int TotalRequests => Proxies.Sum(p => p.TotalRequests);
        public int TotalSuccess => Proxies.Sum(p => p.SuccessCount);
        public int TotalFailed => TotalRequests - TotalSuccess;
        public double OverallSuccessRate => TotalRequests == 0 ? 0 : 100.0 * TotalSuccess / TotalRequests;
    }
}
