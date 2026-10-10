namespace Affiliate.Noon
{
    /// <summary>Catalog data of one Noon listing page (read by <see cref="NoonHtmlParser"/>).</summary>
    public sealed class NoonCatalogResponse
    {
        public int NbHits { get; set; }
        public int NbPages { get; set; }
        public List<NoonHit> Hits { get; set; } = [];
    }

    public sealed class NoonHit
    {
        public string? Sku { get; set; }
        public string? Name { get; set; }
        public string? Brand { get; set; }
        public string? BrandCode { get; set; }

        /// <summary>List (was) price.</summary>
        public decimal? Price { get; set; }

        /// <summary>Discounted price; null when not on sale.</summary>
        public decimal? SalePrice { get; set; }

        /// <summary>Relative product link, e.g. <c>/name/N70259009V/p/?o=offer</c>.</summary>
        public string? PdpUrl { get; set; }

        public string? ImageUrl { get; set; }
        public string? StoreName { get; set; }
        public bool? IsBestSeller { get; set; }
        public NoonRating? ProductRating { get; set; }

        public decimal? CurrentPrice => SalePrice ?? Price;
    }

    public sealed class NoonRating
    {
        public double? Value { get; set; }
        public int? Count { get; set; }
    }
}
