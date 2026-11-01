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

        /// <summary>Requests routed through Google Translate.</summary>
        public int GoogleSuccessCount { get; set; }
        public int GoogleFailedCount { get; set; }

        /// <summary>Requests sent straight to Amazon.</summary>
        public int AmazonSuccessCount { get; set; }
        public int AmazonFailedCount { get; set; }

        public int GoogleTotal => GoogleSuccessCount + GoogleFailedCount;
        public int AmazonTotal => AmazonSuccessCount + AmazonFailedCount;

        public double SuccessRate =>
            TotalRequests == 0 ? 0 : 100.0 * SuccessCount / TotalRequests;

        public double GoogleSuccessRate =>
            GoogleTotal == 0 ? 0 : 100.0 * GoogleSuccessCount / GoogleTotal;

        public double AmazonSuccessRate =>
            AmazonTotal == 0 ? 0 : 100.0 * AmazonSuccessCount / AmazonTotal;
    }

    public class PortStatisticsViewModel
    {
        public PortStatisticsFilterViewModel Filter { get; set; } = new();
        public IReadOnlyList<PortStatisticsRow> Ports { get; set; } = [];

        public int TotalRequests { get; set; }
        public int TotalSuccess { get; set; }
        public int TotalFailed { get; set; }

        public int TotalGoogleSuccess { get; set; }
        public int TotalGoogleFailed { get; set; }
        public int TotalAmazonSuccess { get; set; }
        public int TotalAmazonFailed { get; set; }

        public double OverallSuccessRate =>
            TotalRequests == 0 ? 0 : 100.0 * TotalSuccess / TotalRequests;

        public int TotalGoogleRequests => TotalGoogleSuccess + TotalGoogleFailed;
        public int TotalAmazonRequests => TotalAmazonSuccess + TotalAmazonFailed;

        public double OverallGoogleSuccessRate =>
            TotalGoogleRequests == 0 ? 0 : 100.0 * TotalGoogleSuccess / TotalGoogleRequests;

        public double OverallAmazonSuccessRate =>
            TotalAmazonRequests == 0 ? 0 : 100.0 * TotalAmazonSuccess / TotalAmazonRequests;
    }
}
