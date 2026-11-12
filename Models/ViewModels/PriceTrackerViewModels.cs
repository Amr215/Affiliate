namespace Affiliate.ViewModels
{
    public class PriceTrackerProductViewModel
    {
        public string Lang { get; set; } = "ar";
        public bool IsArabic => string.Equals(Lang, "ar", StringComparison.OrdinalIgnoreCase);
        public string Asin { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? NameAr { get; set; }
        public string? DescriptionAr { get; set; }
        public string? DescriptionEn { get; set; }
        public string? ImageUrl { get; set; }
        public double? Rating { get; set; }
        public int? ReviewsCount { get; set; }
        public string? Currency { get; set; }
        public string Slug { get; set; } = "product";
        public string BuyUrl { get; set; } = "#";
        /// <summary>True when we show last detected price instead of a live current price.</summary>
        public bool ShowLastDetected { get; set; }
        /// <summary>UTC time of the last recorded price (history or last known).</summary>
        public DateTime? LastDetectedAt { get; set; }
        public PriceTrackerStatsSnapshot Stats { get; set; } = new();
        public IReadOnlyList<PublicDealItem> RelatedProducts { get; set; } = [];
        public string? RelatedScraperName { get; set; }

        public string DisplayName(bool arabic) =>
            arabic && !string.IsNullOrWhiteSpace(NameAr) ? NameAr! : Name;

        public string? DisplayDescription(bool arabic) =>
            arabic
                ? (!string.IsNullOrWhiteSpace(DescriptionAr) ? DescriptionAr
                    : !string.IsNullOrWhiteSpace(DescriptionEn) ? DescriptionEn : null)
                : (!string.IsNullOrWhiteSpace(DescriptionEn) ? DescriptionEn
                    : !string.IsNullOrWhiteSpace(DescriptionAr) ? DescriptionAr : null);
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
