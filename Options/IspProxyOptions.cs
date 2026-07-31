namespace Affiliate.Options
{
    /// <summary>Oxylabs ISP proxy settings. Sticky ports (8001+) are round-robined per scrape job.</summary>
    public class IspProxyOptions
    {
        public const string SectionName = "IspProxy";

        /// <summary>When false, pages are fetched directly from this PC (no proxy).</summary>
        public bool Enabled { get; set; }

        /// <summary>Proxy host, e.g. isp.oxylabs.io</summary>
        public string? Host { get; set; }

        /// <summary>
        /// Sticky ports to rotate across (e.g. 8001–8010). Each port is one IP.
        /// Port 8000 rotates randomly and is not recommended for sticky jobs.
        /// </summary>
        public int[] Ports { get; set; } = [8001, 8002, 8003, 8004, 8005, 8006, 8007, 8008, 8009, 8010];

        /// <summary>Fallback single port when <see cref="Ports"/> is empty.</summary>
        public int Port { get; set; } = 8001;

        /// <summary>Oxylabs proxy user, usually prefixed with <c>user-</c>.</summary>
        public string? Username { get; set; }

        public string? Password { get; set; }

        /// <summary>
        /// After a bad response (captcha/blank/block), keep that IP out of rotation
        /// for this many minutes.
        /// </summary>
        public int BadIpCooldownoutMinutes { get; set; } = 45;

        /// <summary>
        /// Short cooldown for a dropped/failed connection (no HTTP response). Network blips must not
        /// burn an IP for the full <see cref="BadIpCooldownoutMinutes"/>.
        /// </summary>
        public int TransientCooldownSeconds { get; set; } = 90;

        /// <summary>Consecutive transport failures on one IP before it gets the full cooldown.</summary>
        public int TransientStrikesBeforeQuarantine { get; set; } = 3;

        /// <summary>Retries on the same IP for a dropped connection before rotating to another IP.</summary>
        public int TransportRetriesPerIp { get; set; } = 2;

        /// <summary>TCP/TLS connect timeout in seconds (fail fast instead of hanging on a dead port).</summary>
        public int ConnectTimeoutSeconds { get; set; } = 20;

        /// <summary>Overall per-request timeout in seconds.</summary>
        public int RequestTimeoutSeconds { get; set; } = 90;
    }
}
