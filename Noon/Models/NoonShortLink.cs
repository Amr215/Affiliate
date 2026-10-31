namespace Affiliate.Noon.Models
{
    /// <summary>
    /// Opaque short code for non-product Noon URLs (brand, search, deals, etc.).
    /// Public path: <c>/n/{Code}</c> → <see cref="DestinationUrl"/>.
    /// </summary>
    public class NoonShortLink
    {
        public int Id { get; set; }

        /// <summary>8-char base62 code (same length as Amazon <c>/u/…</c>, different path).</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>Normalized noon.com absolute URL.</summary>
        public string DestinationUrl { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
