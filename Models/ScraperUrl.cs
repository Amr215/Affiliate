using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Affiliate.Models
{
    /// <summary>
    /// Amazon search (or listing) page to scrape by full URL via ISP proxy.
    /// Filters, sort, and merchant are encoded in the URL itself.
    /// </summary>
    public class ScraperUrl
    {
        public int Id { get; set; }

        /// <summary>Display label in admin / logs.</summary>
        [Required]
        [StringLength(200)]
        [Display(Name = "Name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Amazon TLD used when building product links (e.g. eg).</summary>
        [StringLength(16)]
        public string Domain { get; set; } = "eg";

        /// <summary>
        /// Full Amazon page URL to fetch, e.g.
        /// https://www.amazon.eg/-/en/s?k=...&amp;rh=...&amp;page=1
        /// </summary>
        [Required]
        [StringLength(4000)]
        [Display(Name = "URL")]
        public string Url { get; set; } = string.Empty;

        /// <summary>First results page to fetch (appended/replaced as <c>page</c> on <see cref="Url"/>).</summary>
        public int StartPage { get; set; } = 1;

        /// <summary>Minimum time between runs of this URL.</summary>
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
