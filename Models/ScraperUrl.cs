using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

namespace Affiliate.Models
{
    /// <summary>Product category of a URL scrape; selects the Telegram group its price drops go to.</summary>
    public enum ScraperCategory
    {
        [Display(Name = "أطعمة ومشروبات ومنظفات")]
        FoodDrinksAndCleaning = 1,

        [Display(Name = "أزياء وساعات")]
        FashionAndWatches = 2,

        [Display(Name = "أجهزة")]
        Devices = 3
    }

    public static class ScraperCategoryExtensions
    {
        /// <summary>Label from the member's <see cref="DisplayAttribute"/>, matching what dropdowns show.</summary>
        public static string DisplayName(this ScraperCategory category) =>
            typeof(ScraperCategory).GetField(category.ToString())
                ?.GetCustomAttribute<DisplayAttribute>()?.Name
            ?? category.ToString();
    }

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

        /// <summary>Telegram routing category. When null, price drops of its products are never published.</summary>
        [Display(Name = "التصنيف")]
        public ScraperCategory? Category { get; set; }

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
