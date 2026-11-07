namespace Affiliate.Services
{
    /// <summary>
    /// Tracker list ranking:
    /// 0 = new all-time low (current strictly below every prior recorded price),
    /// 1 = at all-time low (tied with a prior low),
    /// 2 = closest to all-time low.
    /// </summary>
    public static class PriceTrackerSort
    {
        public const int TierNewAllTimeLow = 0;
        public const int TierAllTimeLow = 1;
        public const int TierClosestToAllTimeLow = 2;

        /// <summary>
        /// Returns (tier, rank). For tiers 0–1, rank is % gap vs second-lowest (higher first).
        /// For tier 2, rank is absolute gap above all-time low (lower first).
        /// </summary>
        public static (int Tier, decimal Rank) Compute(
            decimal current,
            IReadOnlyList<decimal> historicalPrices,
            decimal? storedLowest)
        {
            if (current <= 0)
                return (TierClosestToAllTimeLow, decimal.MaxValue);

            var allTimeLow = current;
            if (historicalPrices.Count > 0)
                allTimeLow = Math.Min(allTimeLow, historicalPrices.Min());
            if (storedLowest is > 0)
                allTimeLow = Math.Min(allTimeLow, storedLowest.Value);

            var atAllTimeLow = current <= allTimeLow;
            var isNewAllTimeLow = historicalPrices.Count == 0
                || historicalPrices.All(p => p > current);

            if (atAllTimeLow && isNewAllTimeLow)
                return (TierNewAllTimeLow, PercentBelowSecondLowest(current, historicalPrices));

            if (atAllTimeLow)
                return (TierAllTimeLow, PercentBelowSecondLowest(current, historicalPrices));

            return (TierClosestToAllTimeLow, current - allTimeLow);
        }

        /// <summary>
        /// % difference between the second-lowest historical price and the current price:
        /// (secondLowest − current) / secondLowest × 100.
        /// Uses distinct historical prices only; if none, returns 0.
        /// </summary>
        private static decimal PercentBelowSecondLowest(decimal current, IReadOnlyList<decimal> historicalPrices)
        {
            if (historicalPrices.Count == 0)
                return 0m;

            var distinct = historicalPrices
                .Where(p => p > 0)
                .Distinct()
                .OrderBy(p => p)
                .ToList();

            if (distinct.Count == 0)
                return 0m;

            // Prefer the lowest historical price that is still above current (the prior floor).
            var second = distinct.FirstOrDefault(p => p > current);
            if (second <= 0)
            {
                // Tied all-time low: use next distinct price above the shared low, if any.
                second = distinct.FirstOrDefault(p => p > distinct[0]);
                if (second <= 0)
                    return 0m;
            }

            return Math.Round((second - current) / second * 100m, 4, MidpointRounding.AwayFromZero);
        }
    }
}
