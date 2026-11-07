using System.ComponentModel.DataAnnotations;

namespace Affiliate.Models
{
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

        public ICollection<ScraperUrl> ScraperUrls { get; set; } = new List<ScraperUrl>();
    }
}
