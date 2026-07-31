namespace Affiliate.Models
{
    public class PriceHistory
    {
        public int Id { get; set; }
        public int ProductId { get; set; }
        public decimal? Price { get; set; }
        public decimal? PriceUpper { get; set; } // Max price in range
        public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
        public string? Status { get; set; } // Available, OutOfStock, Unavailable
        public string? ErrorMessage { get; set; }
        
        // Additional tracking data
        public double? Rating { get; set; }
        public int? ReviewsCount { get; set; }
        public bool? IsPrime { get; set; }
        public bool? IsSponsored { get; set; }
        public bool? IsBestSeller { get; set; }
        public string? ShippingInformation { get; set; }

        public Product? Product { get; set; }
    }
}
