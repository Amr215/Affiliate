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
    }
}
