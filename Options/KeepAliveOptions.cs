namespace Affiliate.Options
{
    public class KeepAliveOptions
    {
        public const string SectionName = "KeepAlive";

        public bool Enabled { get; set; } = true;

        /// <summary>How often to HTTP-ping /health to reset shared-host idle timeout.</summary>
        public double IntervalMinutes { get; set; } = 5;
    }
}
