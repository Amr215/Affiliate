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

        /// <summary>ASINs per Amazon search request (pipe-joined in <c>k=</c>). Never 1, capped at 48.</summary>
        public int BatchSize { get; set; } = 48;

        /// <summary>
        /// Max available products to pull per poll (split into BatchSize requests).
        /// Throughput per minute is roughly this value when the cycle is 60 seconds.
        /// </summary>
        public int AsinsPerPoll { get; set; } = 480;

        /// <summary>
        /// Batches fetched concurrently, one per proxy IP. Capped at the number of healthy ISP ports,
        /// so this should not exceed <c>IspProxy:Ports</c>.
        /// </summary>
        public int MaxParallelBatches { get; set; } = 10;

        /// <summary>Shortest gap between cycles when a poll overruns its interval.</summary>
        public int MinimumGapSeconds { get; set; } = 3;

        /// <summary>
        /// Proxy IPs tried per batch before giving up for this poll. Kept low so a site-wide block
        /// doesn't multiply into one request per port per batch; failed ASINs are retried next poll
        /// anyway because they keep the oldest LastCheckedAt.
        /// </summary>
        public int MaxAttemptsPerBatch { get; set; } = 3;

        /// <summary>Amazon TLD used in search URLs (e.g. eg).</summary>
        public string Domain { get; set; } = "eg";
    }
}
