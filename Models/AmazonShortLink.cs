namespace Affiliate.Models
{
    /// <summary>
    /// Opaque short code for non-product Amazon.eg URLs (deals, search, etc.).
    /// Public path: <c>/u/{Code}</c> → <see cref="DestinationUrl"/> + Associates tag.
    /// </summary>
    public class AmazonShortLink
    {
        public int Id { get; set; }

        /// <summary>8-char base62 code (distinct from 9-char ASIN product codes).</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>Normalized amazon.eg absolute URL without the Associates tag.</summary>
        public string DestinationUrl { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
