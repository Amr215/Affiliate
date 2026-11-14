using Affiliate.Models;

namespace Affiliate.ViewModels
{
    public class PublicDealsViewModel
    {
        public IReadOnlyList<PublicDealItem> Deals { get; set; } = [];
        public IReadOnlyList<PublicDealTagOption> Tags { get; set; } = [];
        public IReadOnlyList<PublicDealTagGroup> TagGroups { get; set; } = [];
        public int? SelectedTagId { get; set; }
        public SearchTagCategory? SelectedCategory { get; set; }
        public string? Query { get; set; }

        /// <summary><c>best</c> (default), <c>price_asc</c>, or <c>price_desc</c>.</summary>
        public string Sort { get; set; } = "best";

        public decimal? MinPrice { get; set; }
        public decimal? MaxPrice { get; set; }

        public int Page { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
        public int TotalCount { get; set; }

        /// <summary><c>en</c> or <c>ar</c>. Defaults to Arabic.</summary>
        public string Lang { get; set; } = "ar";

        public bool IsArabic => string.Equals(Lang, "ar", StringComparison.OrdinalIgnoreCase);

        public bool HasPriceFilter => MinPrice.HasValue || MaxPrice.HasValue;
    }

    public class PublicDealTagGroup
    {
        public SearchTagCategory Category { get; set; }
        public IReadOnlyList<PublicDealTagOption> Tags { get; set; } = [];

        public string DisplayName(bool arabic) => Category.DisplayName(arabic);
    }

    public class PublicDealTagOption
    {
        public int Id { get; set; }
        public string NameEn { get; set; } = string.Empty;
        public string NameAr { get; set; } = string.Empty;
        public SearchTagCategory Category { get; set; }

        public string DisplayName(bool arabic) =>
            arabic
                ? (string.IsNullOrWhiteSpace(NameAr) ? NameEn : NameAr)
                : (string.IsNullOrWhiteSpace(NameEn) ? NameAr : NameEn);
    }

    public class PublicDealItem
    {
        public int ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? NameAr { get; set; }
        public string? ImageUrl { get; set; }
        public decimal Price { get; set; }
        public string? Currency { get; set; }
        public double? Rating { get; set; }
        public int? ReviewsCount { get; set; }

        /// <summary>
        /// Card link: affiliate short URL for deals, tracker detail URL for tracker fillers.
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>Tracker page path for JSON-LD (card <see cref="Url"/> may be /d/ redirect).</summary>
        public string SearchListingUrl { get; set; } = string.Empty;

        /// <summary>True when this row is an external-price deal (listed before tracker products).</summary>
        public bool IsDeal { get; set; }

        /// <summary>True when current price is at the recorded all-time low.</summary>
        public bool IsAtLowestPrice { get; set; }

        /// <summary>Whole days the current price has been the lowest since the last lower recorded price.</summary>
        public int LowestPriceDays { get; set; }

        /// <summary>Recent prices oldest→newest for the card sparkline (empty when unknown).</summary>
        public IReadOnlyList<decimal> PriceSpark { get; set; } = [];

        public string DisplayName(bool arabic) =>
            arabic && !string.IsNullOrWhiteSpace(NameAr) ? NameAr! : Name;
    }
}
