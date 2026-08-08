namespace Affiliate.Options
{
    /// <summary>Oxylabs ISP proxy settings. Uses a single port (default 8000).</summary>
    public class IspProxyOptions
    {
        public const string SectionName = "IspProxy";

        /// <summary>When false, pages are fetched directly from this PC (no proxy).</summary>
        public bool Enabled { get; set; }

        /// <summary>Proxy host, e.g. isp.oxylabs.io</summary>
        public string? Host { get; set; }

        /// <summary>Proxy port. Port 8000 rotates randomly across Oxylabs IPs.</summary>
        public int Port { get; set; } = 8000;

        /// <summary>Oxylabs proxy user, usually prefixed with <c>user-</c>.</summary>
        public string? Username { get; set; }

        public string? Password { get; set; }

        /// <summary>Retries on the same connection for a dropped request before failing.</summary>
        public int TransportRetriesPerIp { get; set; } = 2;

        /// <summary>TCP/TLS connect timeout in seconds (fail fast instead of hanging on a dead port).</summary>
        public int ConnectTimeoutSeconds { get; set; } = 20;

        /// <summary>Overall per-request timeout in seconds.</summary>
        public int RequestTimeoutSeconds { get; set; } = 90;
    }
}
