namespace Affiliate.Options
{
    public class AsinRecheckOptions
    {
        public const string SectionName = "AsinRecheck";

        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Minimum seconds for one full poll cycle (inclusive). Time spent doing the work counts
        /// towards this, so the cycle length is the target rather than an idle gap.
        /// </summary>
        public int PollIntervalMinSeconds { get; set; } = 55;

        /// <summary>Maximum seconds for one full poll cycle (inclusive).</summary>
        public int PollIntervalMaxSeconds { get; set; } = 65;

        /// <summary>ASINs per Amazon search request (pipe-joined in <c>k=</c>). Never 1, capped at 200.</summary>
        public int BatchSize { get; set; } = 200;

        /// <summary>
        /// Max available products to pull per poll (split into BatchSize requests).
        /// Throughput per minute is roughly this value when the cycle is 60 seconds.
        /// </summary>
        public int AsinsPerPoll { get; set; } = 1000;

        /// <summary>Batches fetched concurrently within one poll.</summary>
        public int MaxParallelBatches { get; set; } = 10;

        /// <summary>Shortest gap between cycles when a poll overruns its interval.</summary>
        public int MinimumGapSeconds { get; set; } = 3;

        /// <summary>
        /// How often to take a longer cool-down pause (minutes of active work).
        /// Pause time itself does not count — after a cool-down ends, a full interval of
        /// active polling runs again. Used to reduce proxy blocks. Set to 0 to disable.
        /// </summary>
        public int CoolDownEveryMinutes { get; set; } = 6;

        /// <summary>Minimum cool-down pause length in seconds (inclusive).</summary>
        public int CoolDownMinSeconds { get; set; } = 40;

        /// <summary>Maximum cool-down pause length in seconds (inclusive).</summary>
        public int CoolDownMaxSeconds { get; set; } = 90;

        /// <summary>
        /// Attempts per batch page before giving up for this poll. Failed ASINs are retried next
        /// poll because they keep the oldest LastCheckedAt.
        /// </summary>
        public int MaxAttemptsPerBatch { get; set; } = 3;

        /// <summary>Amazon TLD used in search URLs (e.g. eg).</summary>
        public string Domain { get; set; } = "eg";

        /// <summary>
        /// Seller filter for ASIN batch search (<c>rh=p_6:...</c>). Amazon.eg retail is
        /// <c>A1ZVRGNO5AYLOV</c>. Empty skips the filter.
        /// </summary>
        public string MerchantId { get; set; } = "A1ZVRGNO5AYLOV";

        /// <summary>Google Translate route used by proxy ports that Amazon is currently blocking.</summary>
        public AsinRecheckTranslateOptions Translate { get; set; } = new();
    }

    /// <summary>
    /// Routes blocked proxy ports through Google's translate proxy
    /// (<c>www-amazon-eg.translate.goog</c>) instead of leaving them idle for the block window.
    /// Applies to ASIN recheck only.
    /// </summary>
    public class AsinRecheckTranslateOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// <c>_x_tr_sl</c>. The Amazon page is requested in English already, so this stays
        /// <c>auto</c> and translation is effectively a no-op.
        /// </summary>
        public string SourceLanguage { get; set; } = "auto";

        /// <summary>
        /// <c>_x_tr_tl</c>. Must stay <c>en</c> — the parser reads English prices, ratings
        /// ("out of 5") and currency codes, so an Arabic page cannot be parsed.
        /// </summary>
        public string TargetLanguage { get; set; } = "en";

        /// <summary>Google UI language (<c>_x_tr_hl</c>).</summary>
        public string InterfaceLanguage { get; set; } = "en";

        /// <summary>
        /// Amazon's own language cookie/param (<c>language=en_AE</c>) so Amazon serves English
        /// before Google ever sees the page. Empty skips it.
        /// </summary>
        public string AmazonLanguage { get; set; } = "en_AE";
    }
}
