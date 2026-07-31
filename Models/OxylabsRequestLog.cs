using System.ComponentModel.DataAnnotations;

namespace Affiliate.Models
{
    /// <summary>Audit log of each Oxylabs API call made for a <see cref="ScraperSearch"/>.</summary>
    public class OxylabsRequestLog
    {
        public long Id { get; set; }

        /// <summary>Null when the call was an ASIN batch re-check (not a keyword search).</summary>
        public int? ScraperSearchId { get; set; }
        public ScraperSearch? ScraperSearch { get; set; }

        /// <summary>Amazon search page requested (<c>start_page</c>).</summary>
        public int Page { get; set; }

        /// <summary>UTC time the HTTP request was sent.</summary>
        public DateTime RequestedAt { get; set; }

        /// <summary>HTTP status code returned by Oxylabs (e.g. 200).</summary>
        public int StatusCode { get; set; }

        /// <summary>JSON request payload sent to Oxylabs.</summary>
        public string? RequestBody { get; set; }

        /// <summary>Full response body — stored only when <see cref="StatusCode"/> is not 200.</summary>
        public string? ResponseBody { get; set; }

        [StringLength(64)]
        public string? StatusPhrase { get; set; }
    }
}
