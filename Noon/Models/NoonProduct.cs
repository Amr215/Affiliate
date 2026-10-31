namespace Affiliate.Noon.Models
{
    public class NoonProduct
    {
        public int Id { get; set; }

        /// <summary>Noon product key (e.g. N70259009V or partner ZFC…); unique.</summary>
        public string Sku { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
        public string? Brand { get; set; }
        public string Url { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public string? StoreName { get; set; }
        public string Currency { get; set; } = "EGP";

        /// <summary>Selling price: the sale price when there is one, otherwise the list price.</summary>
        public decimal? CurrentPrice { get; set; }

        /// <summary>List (was) price shown crossed out on Noon.</summary>
        public decimal? OriginalPrice { get; set; }

        public decimal? LowestPrice { get; set; }
        public decimal? HighestPrice { get; set; }

        /// <summary>Percent drop of <see cref="CurrentPrice"/> vs <see cref="DropBaselinePrice"/>.</summary>
        public decimal? DropPercent { get; set; }

        /// <summary>First observed price; drop % is always measured against it.</summary>
        public decimal? DropBaselinePrice { get; set; }

        public decimal? LastDropAlertPercent { get; set; }

        public bool IsAvailable { get; set; } = true;

        /// <summary>When true, scrapes never update this product or alert on it.</summary>
        public bool IsBlocked { get; set; }

        public DateTime? NotAvailableDate { get; set; }

        public double? Rating { get; set; }
        public int? ReviewsCount { get; set; }
        public bool IsBestSeller { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastCheckedAt { get; set; }

        public int? NoonBrandUrlId { get; set; }
        public NoonBrandUrl? NoonBrandUrl { get; set; }

        public ICollection<NoonPriceHistory> PriceHistory { get; set; } = new List<NoonPriceHistory>();
    }
}
