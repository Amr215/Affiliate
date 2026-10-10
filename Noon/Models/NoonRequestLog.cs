namespace Affiliate.Noon.Models
{
    public enum NoonRequestOutcome
    {
        Success = 0,
        HttpError = 1,
        Timeout = 2,
        Transport = 3,
        InvalidResponse = 4
    }

    /// <summary>One HTTP attempt for a Noon listing page (retries get their own rows).</summary>
    public class NoonRequestLog
    {
        public long Id { get; set; }

        public int? NoonBrandUrlId { get; set; }
        public NoonBrandUrl? NoonBrandUrl { get; set; }

        public int Page { get; set; }
        public int Attempt { get; set; }

        /// <summary>host:port of the ISP proxy (never the credentials).</summary>
        public string? Proxy { get; set; }

        public DateTime RequestedAt { get; set; }
        public int DurationMs { get; set; }

        /// <summary>HTTP status, or 0 when no response arrived (timeout / connection error).</summary>
        public int StatusCode { get; set; }

        public NoonRequestOutcome Outcome { get; set; }
        public string RequestUrl { get; set; } = string.Empty;

        public int? NbHits { get; set; }
        public int? NbPages { get; set; }

        public string? ErrorMessage { get; set; }

        /// <summary>Stored for failures only (truncated), to keep the table small.</summary>
        public string? ResponseBody { get; set; }
    }
}
