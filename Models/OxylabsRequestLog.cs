using System.ComponentModel.DataAnnotations;

namespace Affiliate.Models
{
    /// <summary>Audit log of each scrape HTTP call (URL page fetch or Oxylabs ASIN batch).</summary>
    public class OxylabsRequestLog
    {
        public long Id { get; set; }

        /// <summary>Null when the call was an ASIN batch re-check (not a URL page scrape).</summary>
        public int? ScraperUrlId { get; set; }
        public ScraperUrl? ScraperUrl { get; set; }

        /// <summary>Amazon search page requested.</summary>
        public int Page { get; set; }

        /// <summary>
        /// ISP proxy port used for the request (e.g. 8000).
        /// Null for ASIN Oxylabs API calls or direct (no-proxy) fetches.
        /// </summary>
        public int? Port { get; set; }

        /// <summary>UTC time the HTTP request was sent.</summary>
        public DateTime RequestedAt { get; set; }

        /// <summary>HTTP status code returned by the upstream (e.g. 200).</summary>
        public int StatusCode { get; set; }

        /// <summary>Request payload or URL that was fetched.</summary>
        public string? RequestBody { get; set; }

        /// <summary>
        /// Upstream response body when the request failed or was rejected.
        /// For Amazon HTML responses this is the raw page (not an app error message), so it can be re-parsed later.
        /// </summary>
        public string? ResponseBody { get; set; }

        [StringLength(64)]
        public string? StatusPhrase { get; set; }
    }
}
