namespace Affiliate.ViewModels
{
    public class PriceTrackerIndexViewModel
    {
        public string Lang { get; set; } = "ar";
        public bool IsArabic => string.Equals(Lang, "ar", StringComparison.OrdinalIgnoreCase);
        public string? Query { get; set; }
        public int? SelectedTagId { get; set; }
        public IReadOnlyList<PublicDealTagOption> Tags { get; set; } = [];
        public int Page { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
        public int TotalCount { get; set; }
        public IReadOnlyList<PriceTrackerListItem> Items { get; set; } = [];
    }

    public class PriceTrackerListItem
    {
        public string Asin { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? NameAr { get; set; }
        public string? ImageUrl { get; set; }
        public decimal? CurrentPrice { get; set; }
        public decimal? LowestPrice { get; set; }
        public decimal? HighestPrice { get; set; }
        public string? Currency { get; set; }
        public string Slug { get; set; } = "product";
        public bool IsLowestIn30DaysOrMore { get; set; }
        public bool IsLowestEver { get; set; }
        public int LowestPriceDays { get; set; }

        public string DisplayName(bool arabic) =>
            arabic && !string.IsNullOrWhiteSpace(NameAr) ? NameAr! : Name;
    }

    public class PriceTrackerProductViewModel
    {
        public string Lang { get; set; } = "ar";
        public bool IsArabic => string.Equals(Lang, "ar", StringComparison.OrdinalIgnoreCase);
        public string Asin { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? NameAr { get; set; }
        public string? ImageUrl { get; set; }
        public string? Currency { get; set; }
        public string Slug { get; set; } = "product";
        public string BuyUrl { get; set; } = "#";
        public PriceTrackerStatsSnapshot Stats { get; set; } = new();

        public string DisplayName(bool arabic) =>
            arabic && !string.IsNullOrWhiteSpace(NameAr) ? NameAr! : Name;
    }

    public class PriceTrackerStatsSnapshot
    {
        public decimal? CurrentPrice { get; set; }
        public decimal? LowestPrice { get; set; }
        public decimal? HighestPrice { get; set; }
        public decimal? AveragePrice { get; set; }
        public int LowestPriceDays { get; set; }
        public bool IsLowestEver { get; set; }
        public bool IsLowestIn30DaysOrMore { get; set; }
        public IReadOnlyList<PriceTrackerChartPoint> ChartPoints { get; set; } = [];
    }

    public class PriceTrackerChartPoint
    {
        public string Label { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public string IsoDate { get; set; } = string.Empty;
    }

    public class PriceTrackerUnavailableViewModel
    {
        public string Lang { get; set; } = "ar";
        public bool IsArabic => string.Equals(Lang, "ar", StringComparison.OrdinalIgnoreCase);
        public string Asin { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? NameAr { get; set; }
        public string? ImageUrl { get; set; }

        public string DisplayName(bool arabic) =>
            arabic && !string.IsNullOrWhiteSpace(NameAr) ? NameAr! : Name;
    }
}
