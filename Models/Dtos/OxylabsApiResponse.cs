using System.Text.Json.Serialization;

namespace Affiliate.Models.Dtos
{
    public class OxylabsApiResponse
    {
        [JsonPropertyName("job")]
        public JobInfo Job { get; set; }

        [JsonPropertyName("results")]
        public List<ResultData> Results { get; set; } = new();
    }

    public class JobInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; }
    }

    public class ResultData
    {
        [JsonPropertyName("content")]
        public ContentData Content { get; set; }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; }
    }

    public class ContentData
    {
        [JsonPropertyName("last_visible_page")]
        public int? LastVisiblePage { get; set; }

        [JsonPropertyName("page")]
        public int? Page { get; set; }

        [JsonPropertyName("results")]
        public ProductResults Results { get; set; }
    }

    public class ProductResults
    {
        [JsonPropertyName("organic")]
        public List<OrganicProduct> Organic { get; set; } = new();
    }

    public class OrganicProduct
    {
        [JsonPropertyName("asin")]
        public string Asin { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("price")]
        public decimal? Price { get; set; }

        [JsonPropertyName("price_upper")]
        public decimal? PriceUpper { get; set; }

        [JsonPropertyName("price_strikethrough")]
        public decimal? PriceStrikethrough { get; set; }

        [JsonPropertyName("currency")]
        public string Currency { get; set; }

        [JsonPropertyName("rating")]
        public double? Rating { get; set; }

        [JsonPropertyName("reviews_count")]
        public int? ReviewsCount { get; set; }

        [JsonPropertyName("is_prime")]
        public bool IsPrime { get; set; }

        [JsonPropertyName("is_sponsored")]
        public bool IsSponsored { get; set; }

        [JsonPropertyName("best_seller")]
        public bool BestSeller { get; set; }

        [JsonPropertyName("manufacturer")]
        public string Manufacturer { get; set; }

        [JsonPropertyName("url_image")]
        public string ImageUrl { get; set; }

        [JsonPropertyName("pos")]
        public int Position { get; set; }

        [JsonPropertyName("shipping_information")]
        public string ShippingInformation { get; set; }
    }
}
