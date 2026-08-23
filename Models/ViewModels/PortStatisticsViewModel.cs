using System.ComponentModel.DataAnnotations;

namespace Affiliate.ViewModels
{
    public class PortStatisticsFilterViewModel
    {
        [DataType(DataType.Date)]
        public DateTime? From { get; set; }

        [DataType(DataType.Date)]
        public DateTime? To { get; set; }
    }

    public class PortStatisticsRow
    {
        /// <summary>Null = direct / Oxylabs API (no ISP proxy port).</summary>
        public int? Port { get; set; }

        public int TotalRequests { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }

        public double SuccessRate =>
            TotalRequests == 0 ? 0 : 100.0 * SuccessCount / TotalRequests;
    }

    public class PortStatisticsViewModel
    {
        public PortStatisticsFilterViewModel Filter { get; set; } = new();
        public IReadOnlyList<PortStatisticsRow> Ports { get; set; } = [];

        public int TotalRequests { get; set; }
        public int TotalSuccess { get; set; }
        public int TotalFailed { get; set; }

        public double OverallSuccessRate =>
            TotalRequests == 0 ? 0 : 100.0 * TotalSuccess / TotalRequests;
    }
}
