using Affiliate.Noon.Models;

namespace Affiliate.Models
{
    /// <summary>Manual one-to-one link between an Amazon product and the same product on Noon.</summary>
    public class ProductLink
    {
        public int Id { get; set; }

        public int ProductId { get; set; }
        public Product? Product { get; set; }

        public int NoonProductId { get; set; }
        public NoonProduct? NoonProduct { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
