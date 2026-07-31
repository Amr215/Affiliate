namespace Affiliate.Options
{
    public class AsinRecheckOptions
    {
        public const string SectionName = "AsinRecheck";

        public bool Enabled { get; set; } = true;

        /// <summary>Seconds between ASIN re-check polls.</summary>
        public double PollIntervalSeconds { get; set; } = 3600;

        /// <summary>Max available products to re-check per poll.</summary>
        public int AsinsPerPoll { get; set; } = 48;

        /// <summary>ASINs per Oxylabs query (pipe-separated).</summary>
        public int BatchSize { get; set; } = 48;

        public string Domain { get; set; } = "eg";
        public string Locale { get; set; } = "en-AE";
    }
}
