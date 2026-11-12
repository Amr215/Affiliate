namespace Affiliate.Options
{
    /// <summary>
    /// Fills <see cref="Affiliate.Models.Product.DescriptionAr"/> and
    /// <see cref="Affiliate.Models.Product.DescriptionEn"/> from Amazon product detail pages
    /// through ISP proxies. Isolated from ASIN recheck / URL scrape / Arabic name fill.
    /// </summary>
    public class ProductDescriptionFillOptions
    {
        public const string SectionName = "ProductDescriptionFill";

        public bool Enabled { get; set; } = true;

        /// <summary>Hours between full runs (default 24).</summary>
        public int IntervalHours { get; set; } = 24;

        /// <summary>Max products missing a description to process in one run.</summary>
        public int MaxProductsPerRun { get; set; } = 20000;

        /// <summary>Concurrent ASIN PDP fetches within one run.</summary>
        public int MaxParallel { get; set; } = 20;

        /// <summary>Attempts per language page before giving up for this ASIN this run.</summary>
        public int MaxAttemptsPerPage { get; set; } = 30;

        /// <summary>Pause between Arabic and English PDP fetch for the same ASIN (ms). 0 = none.</summary>
        public int DelayBetweenLanguagesMs { get; set; } = 150;

        /// <summary>Stagger offset between parallel workers (ms per slot).</summary>
        public int BatchStaggerMs { get; set; } = 25;

        /// <summary>Delay before first request on a new proxy connection (ms).</summary>
        public int WarmupDelayMs { get; set; } = 400;

        /// <summary>Skip Amazon home warmup (faster, more bot risk).</summary>
        public bool SkipHomeWarmup { get; set; }

        /// <summary>Amazon TLD (e.g. eg).</summary>
        public string Domain { get; set; } = "eg";

        /// <summary>Amazon language query value for Arabic PDP.</summary>
        public string AmazonLanguageAr { get; set; } = "ar_AE";

        /// <summary>Amazon language query value for English PDP.</summary>
        public string AmazonLanguageEn { get; set; } = "en_AE";

        /// <summary>Max stored description length (chars).</summary>
        public int MaxDescriptionLength { get; set; } = 4000;

        /// <summary>Seconds to wait after startup before the first run.</summary>
        public int StartupDelaySeconds { get; set; } = 150;
    }
}
