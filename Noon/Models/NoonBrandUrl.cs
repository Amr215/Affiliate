using System.ComponentModel.DataAnnotations;

namespace Affiliate.Noon.Models
{
    /// <summary>One Noon brand (or listing) page, scraped page by page in its own requests.</summary>
    public class NoonBrandUrl
    {
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Browser link (https://www.noon.com/egypt-en/xiaomi/?f[partner][]=p_9404) or the catalog API link.
        /// </summary>
        [Required]
        [StringLength(4000)]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Min drop % vs the previous price to send a Telegram alert. The same value is required
        /// below the average price when the drop is under 10%.
        /// </summary>
        [Range(1, 100)]
        [Display(Name = "حد التنبيه %")]
        public decimal DropAlertPercent { get; set; } = 3m;

        public bool IsEnabled { get; set; } = true;

        public DateTime? LastRunAt { get; set; }
        public string? LastRunError { get; set; }
        public int? LastRunProductCount { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<NoonProduct> Products { get; set; } = new List<NoonProduct>();
    }
}
