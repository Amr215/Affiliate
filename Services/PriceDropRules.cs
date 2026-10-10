namespace Affiliate.Services
{
    /// <summary>Drop-alert criteria shared by the Amazon and Noon scrapers.</summary>
    public static class PriceDropRules
    {
        public const decimal PriceDropAlertThresholdPercent = 3m;

        /// <summary>Below this drop %, an alert also requires the new price to beat the average price.</summary>
        public const decimal AveragePriceGateBelowPercent = 10m;

        /// <summary>How far below the average price the new price must sit to pass the gate.</summary>
        public const decimal MinimumBelowAveragePercent = 3m;

        private const int HistoryEnds = 5;

        public static bool TryPercentOff(decimal original, decimal current, out decimal percent)
        {
            percent = 0;
            if (original <= 0 || current < 0 || current >= original)
                return false;
            percent = (original - current) / original * 100m;
            return true;
        }

        public static decimal RoundPct(decimal percent) =>
            Math.Round(percent, 2, MidpointRounding.AwayFromZero);

        /// <summary>Drop % vs the previous price when it reaches the alert threshold, otherwise null.</summary>
        public static decimal? AlertDropPercent(decimal? previousPrice, decimal? newPrice)
        {
            if (previousPrice is not > 0 || newPrice is not > 0 ||
                !TryPercentOff(previousPrice.Value, newPrice.Value, out var drop))
                return null;

            drop = RoundPct(drop);
            return drop >= PriceDropAlertThresholdPercent ? drop : null;
        }

        /// <summary>
        /// Drops of 10% or more always alert. Smaller drops alert only when the new price is also
        /// at least 3% below the product's average recorded price, so noise around a high baseline is skipped.
        /// Requires <see cref="ApplyHistory"/> to have run first.
        /// </summary>
        public static bool QualifiesForAlert(PriceDropAlert alert)
        {
            if (alert.DropPercent >= AveragePriceGateBelowPercent)
                return true;

            return alert.AveragePrice is > 0
                && alert.CurrentPrice is > 0
                && TryPercentOff(alert.AveragePrice.Value, alert.CurrentPrice.Value, out var belowAverage)
                && RoundPct(belowAverage) >= MinimumBelowAveragePercent;
        }

        /// <summary>Fills the history, average, lowest and lowest-since fields from a product's recorded prices (oldest first).</summary>
        public static void ApplyHistory(PriceDropAlert alert, IReadOnlyList<(DateTime CheckedAt, decimal Price)> points)
        {
            if (points.Count == 0)
                return;

            var all = points.Select(p => new PriceHistoryPoint(string.Empty, p.Price, p.CheckedAt)).ToList();
            var truncated = all.Count > HistoryEnds * 2;

            alert.History = truncated ? all.Take(HistoryEnds).Concat(all.TakeLast(HistoryEnds)).ToList() : all;
            alert.HistoryTruncated = truncated;
            alert.AveragePrice = Math.Round(points.Average(p => p.Price), 2, MidpointRounding.AwayFromZero);
            alert.LowestPrice = points.Min(p => p.Price);

            // History only records changes, so the latest price has been the lowest since the point after the last lower one.
            var lowerIndex = all.FindLastIndex(p => p.Price < all[^1].Price);
            alert.LowestPriceDays = (DateTime.UtcNow - points[lowerIndex + 1].CheckedAt).Days;
            alert.IsLowestEver = lowerIndex < 0;
        }
    }
}
