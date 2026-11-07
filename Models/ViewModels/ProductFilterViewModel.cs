using System.ComponentModel.DataAnnotations;
using Affiliate.Models;

namespace Affiliate.ViewModels
{
    public class ProductFilterViewModel
    {
        [Display(Name = "Search")]
        public string? Search { get; set; }

        [Display(Name = "ASIN")]
        public string? Asin { get; set; }

        [Display(Name = "Scraper URL")]
        public int? ScraperUrlId { get; set; }

        public string? Manufacturer { get; set; }
        public string? Currency { get; set; }
        public string? Status { get; set; }

        [Display(Name = "Available")]
        public bool? IsAvailable { get; set; }

        [Display(Name = "Blocked")]
        public bool? IsBlocked { get; set; }

        [Display(Name = "Has unavailable date")]
        public bool HasNotAvailableDate { get; set; }

        [Display(Name = "Prime")]
        public bool? IsPrime { get; set; }

        [Display(Name = "Sponsored")]
        public bool? IsSponsored { get; set; }

        [Display(Name = "Best seller")]
        public bool? IsBestSeller { get; set; }

        [Display(Name = "Min price")]
        public int? MinPrice { get; set; }

        [Display(Name = "Max price")]
        public int? MaxPrice { get; set; }

        [Display(Name = "Min drop %")]
        public decimal? MinDropPercent { get; set; }

        [Display(Name = "Min rating")]
        public double? MinRating { get; set; }

        [Display(Name = "Min reviews")]
        public int? MinReviews { get; set; }

        [Display(Name = "Checked from (UTC)")]
        [DataType(DataType.Date)]
        public DateTime? LastCheckedFrom { get; set; }

        [Display(Name = "Checked to (UTC)")]
        [DataType(DataType.Date)]
        public DateTime? LastCheckedTo { get; set; }

        [Display(Name = "Created from (UTC)")]
        [DataType(DataType.Date)]
        public DateTime? CreatedFrom { get; set; }

        [Display(Name = "Created to (UTC)")]
        [DataType(DataType.Date)]
        public DateTime? CreatedTo { get; set; }

        [Display(Name = "Sort by")]
        public string SortBy { get; set; } = "DropPercent";

        [Display(Name = "Direction")]
        public string SortDir { get; set; } = "desc";

        public int Page { get; set; } = 1;

        [Display(Name = "Page size")]
        public int PageSize { get; set; } = 100;

        /// <summary>Non-empty filter values as query-string route data for the given page.</summary>
        public Dictionary<string, string?> ToRoute(int page)
        {
            var d = new Dictionary<string, string?>
            {
                ["Page"] = page.ToString(),
                ["PageSize"] = PageSize.ToString(),
                ["SortBy"] = SortBy,
                ["SortDir"] = SortDir,
                ["Search"] = Search,
                ["Asin"] = Asin,
                ["ScraperUrlId"] = ScraperUrlId?.ToString(),
                ["Manufacturer"] = Manufacturer,
                ["Currency"] = Currency,
                ["Status"] = Status,
                ["IsAvailable"] = IsAvailable?.ToString().ToLowerInvariant(),
                ["IsBlocked"] = IsBlocked?.ToString().ToLowerInvariant(),
                ["HasNotAvailableDate"] = HasNotAvailableDate ? "true" : null,
                ["IsPrime"] = IsPrime?.ToString().ToLowerInvariant(),
                ["IsSponsored"] = IsSponsored?.ToString().ToLowerInvariant(),
                ["IsBestSeller"] = IsBestSeller?.ToString().ToLowerInvariant(),
                ["MinPrice"] = MinPrice?.ToString(),
                ["MaxPrice"] = MaxPrice?.ToString(),
                ["MinDropPercent"] = MinDropPercent?.ToString(),
                ["MinRating"] = MinRating?.ToString(),
                ["MinReviews"] = MinReviews?.ToString(),
                ["LastCheckedFrom"] = LastCheckedFrom?.ToString("yyyy-MM-dd"),
                ["LastCheckedTo"] = LastCheckedTo?.ToString("yyyy-MM-dd"),
                ["CreatedFrom"] = CreatedFrom?.ToString("yyyy-MM-dd"),
                ["CreatedTo"] = CreatedTo?.ToString("yyyy-MM-dd")
            };

            return d.Where(kv => !string.IsNullOrEmpty(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }
    }

    public class ProductsIndexViewModel
    {
        public ProductFilterViewModel Filter { get; set; } = new();
        public IReadOnlyList<Product> Products { get; set; } = [];
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
        public IReadOnlyList<string> Manufacturers { get; set; } = [];
        public IReadOnlyList<string> Currencies { get; set; } = [];
        public IReadOnlyList<string> Statuses { get; set; } = [];
        public IReadOnlyList<ScraperUrlOption> Searches { get; set; } = [];
    }

    public class ProductDetailsViewModel
    {
        public Product Product { get; set; } = null!;
        public IReadOnlyList<PriceHistory> History { get; set; } = [];

        [Display(Name = "History from (UTC)")]
        [DataType(DataType.Date)]
        public DateTime? HistoryFrom { get; set; }

        [Display(Name = "History to (UTC)")]
        [DataType(DataType.Date)]
        public DateTime? HistoryTo { get; set; }
    }

    public class BulkUpdateAvailabilityRequest
    {
        public int[] Ids { get; set; } = [];
        public bool IsAvailable { get; set; }
        public string? ReturnUrl { get; set; }
    }

    public class BulkUpdateBlockedRequest
    {
        public int[] Ids { get; set; } = [];
        public bool IsBlocked { get; set; }
        public string? ReturnUrl { get; set; }
    }

    public class BulkUpdateProductResult
    {
        public int Id { get; set; }
        public bool IsAvailable { get; set; }
        public bool IsBlocked { get; set; }
        public DateTime? NotAvailableDate { get; set; }
    }
}
