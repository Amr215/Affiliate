using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Affiliate.Models
{
    /// <summary>Top-level group for public deals-page filter tags.</summary>
    public enum SearchTagCategory
    {
        [Display(Name = "إلكترونيات")]
        Electronics = 1,

        [Display(Name = "أجهزة منزلية")]
        HomeAppliances = 2,

        [Display(Name = "مطبخ")]
        Kitchen = 3,

        [Display(Name = "أزياء")]
        Fashion = 4,

        [Display(Name = "جمال وعناية")]
        Beauty = 5,

        [Display(Name = "أطفال")]
        BabyKids = 6,

        [Display(Name = "رياضة")]
        Sports = 7,

        [Display(Name = "سيارات")]
        Automotive = 8,

        [Display(Name = "سوبر ماركت")]
        Supermarket = 9,

        [Display(Name = "عدد صيانة")]
        MaintenanceTools = 10,

        [Display(Name = "أخرى")]
        Other = 99
    }

    public static class SearchTagCategoryExtensions
    {
        /// <summary>Arabic label from <see cref="DisplayAttribute"/> (admin dropdowns).</summary>
        public static string DisplayName(this SearchTagCategory category) =>
            typeof(SearchTagCategory).GetField(category.ToString())
                ?.GetCustomAttribute<DisplayAttribute>()?.Name
            ?? category.ToString();

        /// <summary>Localized label for the public deals page.</summary>
        public static string DisplayName(this SearchTagCategory category, bool arabic) => category switch
        {
            SearchTagCategory.Electronics => arabic ? "إلكترونيات" : "Electronics",
            SearchTagCategory.HomeAppliances => arabic ? "أجهزة منزلية" : "Home Appliances",
            SearchTagCategory.Kitchen => arabic ? "مطبخ" : "Kitchen",
            SearchTagCategory.Fashion => arabic ? "أزياء" : "Fashion",
            SearchTagCategory.Beauty => arabic ? "جمال وعناية" : "Beauty & Care",
            SearchTagCategory.BabyKids => arabic ? "أطفال" : "Baby & Kids",
            SearchTagCategory.Sports => arabic ? "رياضة" : "Sports",
            SearchTagCategory.Automotive => arabic ? "سيارات" : "Automotive",
            SearchTagCategory.Supermarket => arabic ? "سوبر ماركت" : "Supermarket",
            SearchTagCategory.MaintenanceTools => arabic ? "عدد صيانة" : "Maintenance Tools",
            SearchTagCategory.Other => arabic ? "أخرى" : "Other",
            _ => category.ToString()
        };
    }

    /// <summary>Public homepage filter label for products scraped under a <see cref="ScraperUrl"/>.</summary>
    public class SearchTag
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        [Display(Name = "الاسم بالعربي")]
        public string NameAr { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        [Display(Name = "الاسم بالإنجليزي")]
        public string NameEn { get; set; } = string.Empty;

        [Display(Name = "التصنيف")]
        public SearchTagCategory Category { get; set; } = SearchTagCategory.Other;

        [Display(Name = "الترتيب")]
        public int Position { get; set; }

        /// <summary>When false, tag is hidden from public deals filters and its products are excluded from the deals list. Product tracker pages are unaffected.</summary>
        [Display(Name = "إظهار في العروض")]
        public bool ShowInDeals { get; set; } = true;

        public ICollection<ScraperUrl> ScraperUrls { get; set; } = new List<ScraperUrl>();
    }
}
