namespace Affiliate.Models
{
    public class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? Asin { get; set; } // Amazon Standard Identification Number
        public decimal? CurrentPrice { get; set; }
        public decimal? LowestPrice { get; set; }
        public decimal? HighestPrice { get; set; }

        /// <summary>
        /// Percent drop of <see cref="CurrentPrice"/> vs <see cref="DropBaselinePrice"/>
        /// (first observed price). Null when not below baseline.
        /// </summary>
        public decimal? DropPercent { get; set; }

        /// <summary>
        /// First observed price; fixed for the product lifetime. Drop % is always vs this.
        /// </summary>
        public decimal? DropBaselinePrice { get; set; }

        /// <summary>
        /// Drop % (vs previous recorded price) of the last successfully sent Telegram alert.
        /// </summary>
        public decimal? LastDropAlertPercent { get; set; }

        public bool IsAvailable { get; set; } = true;

        /// <summary>
        /// When true, scraper/Oxylabs responses must not create or update this product
        /// (including price history). Null/false = not blocked.
        /// </summary>
        public bool? IsBlocked { get; set; }

        /// <summary>
        /// UTC time the product first stopped returning from ASIN recheck.
        /// Cleared when it returns. Use to manually set <see cref="IsAvailable"/> false after ~1 week.
        /// </summary>
        public DateTime? NotAvailableDate { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastCheckedAt { get; set; }
        public string? Status { get; set; } // Available, OutOfStock, Unavailable
        
        // Additional product information
        public double? Rating { get; set; }
        public int? ReviewsCount { get; set; }
        public bool IsPrime { get; set; }
        public bool IsSponsored { get; set; }
        public bool IsBestSeller { get; set; }
        public string? Currency { get; set; }
        public string? Manufacturer { get; set; }
        public string? ImageUrl { get; set; }
        public int? Position { get; set; } // Position in search results
        public string? ShippingInformation { get; set; }

        /// <summary>
        /// Search that last discovered/updated this product via Oxylabs search.
        /// Null for products only seen through ASIN recheck (or before this field existed).
        /// </summary>
        public int? ScraperSearchId { get; set; }
        public ScraperSearch? ScraperSearch { get; set; }

        public ICollection<PriceHistory> PriceHistory { get; set; } = new List<PriceHistory>();
    }
}
