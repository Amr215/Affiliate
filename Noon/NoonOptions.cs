namespace Affiliate.Noon
{
    public class NoonOptions
    {
        public const string SectionName = "Noon";

        public bool Enabled { get; set; } = true;

        /// <summary>Target length of one cycle over all enabled brands (random between min and max).</summary>
        public int PollIntervalMinSeconds { get; set; } = 35;
        public int PollIntervalMaxSeconds { get; set; } = 45;

        /// <summary>Pause between cycles when a cycle overruns its target.</summary>
        public int MinimumGapSeconds { get; set; } = 3;

        public int CoolDownEveryMinutes { get; set; } = 180;
        public int CoolDownMinSeconds { get; set; } = 15;
        public int CoolDownMaxSeconds { get; set; } = 30;

        /// <summary>Brands scraped at the same time; each brand fetches its pages in its own requests.</summary>
        public int MaxParallelBrands { get; set; } = 10;

        public int MaxPagesPerBrand { get; set; } = 50;

        /// <summary>Pages of one brand fetched at the same time, each through its own proxy.</summary>
        public int MaxParallelPages { get; set; } = 10;

        /// <summary>Tries per page; every retry switches to another proxy.</summary>
        public int MaxAttemptsPerPage { get; set; } = 10;

        public int RequestTimeoutSeconds { get; set; } = 30;

        /// <summary>Noon-only proxy blocking; independent of the Amazon block state.</summary>
        public int ConsecutiveFailuresBeforeBlock { get; set; } = 2;
        public int BlockDurationSeconds { get; set; } = 120;

        /// <summary>Newest <see cref="Models.NoonRequestLog"/> rows kept by the cleanup job.</summary>
        public int RequestLogKeepCount { get; set; } = 5000;

        /// <summary>Path segment of product links, e.g. https://www.noon.com/egypt-en/...</summary>
        public string Locale { get; set; } = "egypt-en";

        public string Currency { get; set; } = "EGP";

        public NoonHeaderOptions Headers { get; set; } = new();
    }

    /// <summary>Browser headers for the listing page request.</summary>
    public class NoonHeaderOptions
    {
        /// <summary>Value of the <c>nloc</c> cookie that selects the Egypt English site.</summary>
        public string LocaleCookie { get; set; } = "en-eg";

        public string UserAgent { get; set; } =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36";

        public string SecChUa { get; set; } =
            "\"Chromium\";v=\"154\", \"Google Chrome\";v=\"154\", \"Not A(Brand\";v=\"99\"";
    }
}
