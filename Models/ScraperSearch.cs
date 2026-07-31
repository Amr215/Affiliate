using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Affiliate.Models
{
    public class ScraperSearch
    {
        public int Id { get; set; }

        /// <summary>Display label in admin / logs.</summary>
        [Required]
        [StringLength(200)]
        [Display(Name = "Name")]
        public string Name { get; set; } = string.Empty;

        [Required]
        [StringLength(64)]
        public string Source { get; set; } = "amazon_search";

        [Required]
        [StringLength(16)]
        public string Domain { get; set; } = "eg";

        [Required]
        [StringLength(500)]
        public string Query { get; set; } = string.Empty;

        [Required]
        [StringLength(32)]
        public string Locale { get; set; } = "ar-EG";

        public int StartPage { get; set; } = 1;
        public bool Parse { get; set; } = true;

        // --- Oxylabs context parameters ---

        [Display(Name = "Force headers")]
        public bool ForceHeaders { get; set; }

        [Display(Name = "Force cookies")]
        public bool ForceCookies { get; set; }

        [Display(Name = "HC policy")]
        public bool HcPolicy { get; set; } = true;

        [StringLength(64)]
        [Display(Name = "Category ID")]
        public string? CategoryId { get; set; }

        [StringLength(64)]
        [Display(Name = "Merchant ID")]
        public string? MerchantId { get; set; }

        [Display(Name = "Check empty geo")]
        public bool? CheckEmptyGeo { get; set; }

        [Display(Name = "Safe search")]
        public bool SafeSearch { get; set; } = true;

        /// <summary>Oxylabs context: currency (e.g. EGP).</summary>
        [StringLength(16)]
        [Display(Name = "Currency")]
        public string? Currency { get; set; }

        /// <summary>Oxylabs context: sort_by (e.g. price_low_to_high).</summary>
        [StringLength(128)]
        [Display(Name = "Sort by")]
        public string? SortBy { get; set; }

        [StringLength(500)]
        [Display(Name = "Refinements")]
        public string? Refinements { get; set; }

        [Display(Name = "Min price")]
        public int? MinPrice { get; set; }

        [Display(Name = "Max price")]
        public int? MaxPrice { get; set; }

        /// <summary>Oxylabs context: geo_location (e.g. Cairo).</summary>
        [StringLength(128)]
        [Display(Name = "Geo location")]
        public string? GeoLocation { get; set; }

        /// <summary>Minimum time between runs of this search.</summary>
        [Range(60, 86400)]
        [Display(Name = "Interval (seconds)")]
        public int IntervalSeconds { get; set; } = 600;

        public bool IsEnabled { get; set; } = true;

        public DateTime? LastRunAt { get; set; }
        public string? LastRunError { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<OxylabsRequestLog> OxylabsRequestLogs { get; set; } = new List<OxylabsRequestLog>();
        public ICollection<Product> Products { get; set; } = new List<Product>();

        /// <summary>In-memory helper over <see cref="IntervalSeconds"/> — not stored in the database.</summary>
        [NotMapped]
        public TimeSpan Interval
        {
            get => TimeSpan.FromSeconds(IntervalSeconds);
            set => IntervalSeconds = (int)Math.Max(1, value.TotalSeconds);
        }

        public bool IsDue(DateTime utcNow) =>
            IsEnabled &&
            (LastRunAt == null || LastRunAt.Value.Add(Interval) <= utcNow);
    }
}
