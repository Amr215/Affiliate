namespace Affiliate.Options
{
    /// <summary>Webshare datacenter proxy settings. Picks a random proxy from <see cref="ProxiesFile"/> per request.</summary>
    public class IspProxyOptions
    {
        public const string SectionName = "IspProxy";

        /// <summary>When false, pages are fetched directly from this PC (no proxy).</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Webshare proxy list file (relative to wwwroot), one entry per line as <c>host:port:username:password</c>.
        /// </summary>
        public string ProxiesFile { get; set; } = "Webshare 100 proxies.txt";

        /// <summary>Retries on the same connection for a dropped request before failing.</summary>
        public int TransportRetriesPerIp { get; set; } = 2;

        /// <summary>TCP/TLS connect timeout in seconds (fail fast instead of hanging on a dead proxy).</summary>
        public int ConnectTimeoutSeconds { get; set; } = 20;

        /// <summary>Overall per-request timeout in seconds.</summary>
        public int RequestTimeoutSeconds { get; set; } = 90;

        /// <summary>
        /// Consecutive failures (with no success in between) before a proxy is temporarily blocked.
        /// </summary>
        public int ConsecutiveFailuresBeforeBlock { get; set; } = 2;

        /// <summary>How long a blocked proxy stays unavailable (seconds).</summary>
        public int BlockDurationSeconds { get; set; } = 80;
    }
}
