namespace Affiliate.Options
{
    /// <summary>
    /// Fills <see cref="Affiliate.Models.Product.NameAr"/> from Amazon Arabic search pages
    /// through ISP proxies. Isolated from ASIN recheck / URL scrape pipelines.
    /// </summary>
    public class ArabicNameFillOptions
    {
        public const string SectionName = "ArabicNameFill";

        public bool Enabled { get; set; } = true;

        /// <summary>Hours between full runs (default 24).</summary>
        public int IntervalHours { get; set; } = 24;

        /// <summary>ASINs per Amazon Arabic search request (pipe-joined in <c>k=</c>).</summary>
        public int BatchSize { get; set; } = 48;

        /// <summary>Max products without <c>NameAr</c> to process in one run.</summary>
        public int MaxProductsPerRun { get; set; } = 5000;

        /// <summary>Batches fetched concurrently within one run.</summary>
        public int MaxParallelBatches { get; set; } = 8;

        /// <summary>Attempts per batch page before giving up for this run.</summary>
        public int MaxAttemptsPerBatch { get; set; } = 20;

        /// <summary>Amazon TLD (e.g. eg).</summary>
        public string Domain { get; set; } = "eg";

        /// <summary>
        /// Seller filter (<c>rh=p_6:...</c>). Amazon.eg retail is <c>A1ZVRGNO5AYLOV</c>.
        /// Empty skips the filter.
        /// </summary>
        public string MerchantId { get; set; } = "A1ZVRGNO5AYLOV";

        /// <summary>Amazon language query value for Arabic pages.</summary>
        public string AmazonLanguage { get; set; } = "ar_AE";

        /// <summary>Seconds to wait after startup before the first run.</summary>
        public int StartupDelaySeconds { get; set; } = 90;
    }
}
