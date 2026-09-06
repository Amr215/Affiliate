namespace Affiliate.Options
{
    /// <summary>Oxylabs ISP proxy settings. Picks a random port in [PortMin, PortMax] per request.</summary>
    public class IspProxyOptions
    {
        public const string SectionName = "IspProxy";

        /// <summary>When false, pages are fetched directly from this PC (no proxy).</summary>
        public bool Enabled { get; set; }

        /// <summary>Proxy host, e.g. isp.oxylabs.io</summary>
        public string? Host { get; set; }

        /// <summary>
        /// Inclusive lower bound of proxy ports (Oxylabs sticky IPs start at 8001).
        /// </summary>
        public int PortMin { get; set; } = 8001;

        /// <summary>
        /// Inclusive upper bound of proxy ports (e.g. 8010 for ten sticky IPs).
        /// </summary>
        public int PortMax { get; set; } = 8010;

        /// <summary>Oxylabs proxy user, usually prefixed with <c>user-</c>.</summary>
        public string? Username { get; set; }

        public string? Password { get; set; }

        /// <summary>Retries on the same connection for a dropped request before failing.</summary>
        public int TransportRetriesPerIp { get; set; } = 2;

        /// <summary>TCP/TLS connect timeout in seconds (fail fast instead of hanging on a dead port).</summary>
        public int ConnectTimeoutSeconds { get; set; } = 20;

        /// <summary>Overall per-request timeout in seconds.</summary>
        public int RequestTimeoutSeconds { get; set; } = 90;

        /// <summary>
        /// Consecutive failures (with no success in between) before a proxy port is temporarily blocked.
        /// </summary>
        public int ConsecutiveFailuresBeforeBlock { get; set; } = 2;

        /// <summary>How long a blocked proxy port stays unavailable (seconds).</summary>
        public int BlockDurationSeconds { get; set; } = 80;
    }
}
