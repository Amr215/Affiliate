using System.ComponentModel.DataAnnotations;

namespace Affiliate.ViewModels
{
    public class IpRangeStatisticsFilterViewModel
    {
        [DataType(DataType.Date)]
        public DateTime? From { get; set; }

        [DataType(DataType.Date)]
        public DateTime? To { get; set; }

        /// <summary>
        /// Hide ranges with fewer Amazon requests than this (avoids ranking tiny samples as "best").
        /// </summary>
        [Range(0, int.MaxValue)]
        public int MinAmazonRequests { get; set; } = 10;
    }

    public class IpRangeStatisticsRow
    {
        /// <summary>IPv4 /24 CIDR, e.g. <c>45.43.167.0/24</c>. Null = unknown / ambiguous port.</summary>
        public string? IpRange { get; set; }

        /// <summary>Configured proxies whose host falls in this /24.</summary>
        public int ProxyCount { get; set; }

        /// <summary>Distinct logged ports attributed to this range.</summary>
        public int PortCount { get; set; }

        public int AmazonSuccessCount { get; set; }
        public int AmazonFailedCount { get; set; }

        public int AmazonTotal => AmazonSuccessCount + AmazonFailedCount;

        public double AmazonSuccessRate =>
            AmazonTotal == 0 ? 0 : 100.0 * AmazonSuccessCount / AmazonTotal;

        /// <summary>True when the port appears on more than one host in the proxy file.</summary>
        public bool IsAmbiguous { get; set; }
    }

    public class IpRangeStatisticsViewModel
    {
        public IpRangeStatisticsFilterViewModel Filter { get; set; } = new();
        public IReadOnlyList<IpRangeStatisticsRow> Ranges { get; set; } = [];

        public int TotalAmazonSuccess { get; set; }
        public int TotalAmazonFailed { get; set; }
        public int AmbiguousPortCount { get; set; }
        public int UnmappedPortCount { get; set; }

        public int TotalAmazonRequests => TotalAmazonSuccess + TotalAmazonFailed;

        public double OverallAmazonSuccessRate =>
            TotalAmazonRequests == 0 ? 0 : 100.0 * TotalAmazonSuccess / TotalAmazonRequests;
    }
}
