namespace Affiliate.Noon.Models
{
    /// <summary>Recorded only when the price changes.</summary>
    public class NoonPriceHistory
    {
        public int Id { get; set; }
        public int NoonProductId { get; set; }
        public decimal? Price { get; set; }
        public decimal? OriginalPrice { get; set; }
        public DateTime CheckedAt { get; set; }

        public NoonProduct? NoonProduct { get; set; }
    }
}
