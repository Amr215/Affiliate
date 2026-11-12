namespace Affiliate.Services
{
    public sealed class PriceTrackerStats
    {
        public decimal? CurrentPrice { get; init; }
        public decimal? LowestPrice { get; init; }
        public decimal? HighestPrice { get; init; }
        public decimal? AveragePrice { get; init; }
        public int LowestPriceDays { get; init; }
        public bool IsLowestEver { get; init; }
        public bool IsLowestIn30DaysOrMore { get; init; }
        public IReadOnlyList<PriceTrackerHistoryPoint> ChartPoints { get; init; } = [];

        /// <summary>
        /// Days since the latest price last beat a prior lower point (history oldest → newest).
        /// <paramref name="isLowestEver"/> is true when no recorded price was below the latest.
        /// </summary>
        public static (int LowestPriceDays, bool IsLowestEver) LowestStreak(
            IReadOnlyList<(DateTime CheckedAt, decimal Price)> pointsOldestFirst)
        {
            if (pointsOldestFirst.Count == 0)
                return (0, false);

            var latest = pointsOldestFirst[^1].Price;
            var lowerIndex = -1;
            for (var i = pointsOldestFirst.Count - 1; i >= 0; i--)
            {
                if (pointsOldestFirst[i].Price < latest)
                {
                    lowerIndex = i;
                    break;
                }
            }

            var days = (DateTime.UtcNow - pointsOldestFirst[lowerIndex + 1].CheckedAt).Days;
            return (days, lowerIndex < 0);
        }

        public static PriceTrackerStats FromHistory(
            decimal? currentPrice,
            decimal? storedLowest,
            decimal? storedHighest,
            IReadOnlyList<(DateTime CheckedAt, decimal Price)> pointsOldestFirst)
        {
            if (pointsOldestFirst.Count == 0)
            {
                return new PriceTrackerStats
                {
                    CurrentPrice = currentPrice,
                    LowestPrice = storedLowest ?? currentPrice,
                    HighestPrice = storedHighest ?? currentPrice,
                    AveragePrice = currentPrice
                };
            }

            var low = pointsOldestFirst.Min(p => p.Price);
            var high = pointsOldestFirst.Max(p => p.Price);
            var avg = Math.Round(pointsOldestFirst.Average(p => p.Price), 2, MidpointRounding.AwayFromZero);
            var latest = pointsOldestFirst[^1].Price;

            var (lowestDays, isLowestEver) = LowestStreak(pointsOldestFirst);

            return new PriceTrackerStats
            {
                CurrentPrice = currentPrice ?? latest,
                LowestPrice = storedLowest is > 0 ? Math.Min(storedLowest.Value, low) : low,
                HighestPrice = storedHighest is > 0 ? Math.Max(storedHighest.Value, high) : high,
                AveragePrice = avg,
                LowestPriceDays = lowestDays,
                IsLowestEver = isLowestEver,
                IsLowestIn30DaysOrMore = isLowestEver || lowestDays >= 30,
                ChartPoints = pointsOldestFirst
                    .Select(p => new PriceTrackerHistoryPoint(p.CheckedAt, p.Price))
                    .ToList()
            };
        }
    }

    public sealed record PriceTrackerHistoryPoint(DateTime CheckedAt, decimal Price);
}
