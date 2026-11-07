using System.Globalization;
using Affiliate.Data;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Services
{
    public enum PriceSparklineTrend
    {
        Flat,
        Down,
        Up
    }

    /// <summary>Small SVG sparkline for product list cards/rows.</summary>
    public sealed record PriceSparklineModel(
        PriceSparklineTrend Trend,
        string PolylinePoints,
        int Width,
        int Height)
    {
        public string CssClass => Trend switch
        {
            PriceSparklineTrend.Down => "is-down",
            PriceSparklineTrend.Up => "is-up",
            _ => "is-flat"
        };

        public string Label(bool arabic) => Trend switch
        {
            PriceSparklineTrend.Down => arabic ? "السعر انخفض" : "Price dropped",
            PriceSparklineTrend.Up => arabic ? "السعر ارتفع" : "Price rose",
            _ => arabic ? "السعر ثابت" : "Price steady"
        };

        public string Arrow => Trend switch
        {
            PriceSparklineTrend.Down => "↓",
            PriceSparklineTrend.Up => "↑",
            _ => "→"
        };
    }

    public static class PriceSparkline
    {
        public const int DefaultMaxPoints = 14;
        public const int DefaultWidth = 72;
        public const int DefaultHeight = 22;

        public static PriceSparklineModel? TryBuild(
            IReadOnlyList<decimal>? prices,
            int width = DefaultWidth,
            int height = DefaultHeight)
        {
            if (prices is null || prices.Count < 2)
                return null;

            var first = prices[0];
            var last = prices[^1];
            var trend = last < first - 0.01m
                ? PriceSparklineTrend.Down
                : last > first + 0.01m
                    ? PriceSparklineTrend.Up
                    : PriceSparklineTrend.Flat;

            var min = prices.Min();
            var max = prices.Max();
            var range = max - min;
            if (range <= 0)
                range = 1;

            // Pad vertically so the stroke is not clipped.
            const float padY = 2f;
            var usableH = Math.Max(1f, height - padY * 2);
            var n = prices.Count;
            var points = new List<string>(n);
            for (var i = 0; i < n; i++)
            {
                var x = n == 1 ? width / 2f : i * (width - 1f) / (n - 1);
                var norm = (float)((prices[i] - min) / range);
                var y = padY + usableH * (1f - norm);
                points.Add(
                    string.Create(CultureInfo.InvariantCulture, $"{x:0.##},{y:0.##}"));
            }

            return new PriceSparklineModel(trend, string.Join(' ', points), width, height);
        }

        /// <summary>
        /// Latest <paramref name="maxPoints"/> Amazon price points per product (oldest → newest).
        /// </summary>
        public static async Task<Dictionary<int, IReadOnlyList<decimal>>> LoadAmazonAsync(
            AffiliateDbContext db,
            IReadOnlyCollection<int> productIds,
            int maxPoints = DefaultMaxPoints,
            CancellationToken cancellationToken = default)
        {
            if (productIds.Count == 0)
                return new Dictionary<int, IReadOnlyList<decimal>>();

            var ids = productIds as IList<int> ?? productIds.ToList();
            var rows = await db.PriceHistories.AsNoTracking()
                .Where(h => ids.Contains(h.ProductId) && h.Price != null && h.Price > 0)
                .OrderByDescending(h => h.CheckedAt)
                .Select(h => new { h.ProductId, Price = h.Price!.Value })
                .ToListAsync(cancellationToken);

            return ToRecentById(rows.Select(r => (r.ProductId, r.Price)), maxPoints);
        }

        private static Dictionary<int, IReadOnlyList<decimal>> ToRecentById(
            IEnumerable<(int Id, decimal Price)> rows,
            int maxPoints)
        {
            var map = new Dictionary<int, List<decimal>>();
            foreach (var (id, price) in rows)
            {
                if (!map.TryGetValue(id, out var list))
                {
                    list = new List<decimal>(maxPoints);
                    map[id] = list;
                }

                if (list.Count >= maxPoints)
                    continue;

                list.Add(price);
            }

            return map.ToDictionary(
                kv => kv.Key,
                kv =>
                {
                    kv.Value.Reverse();
                    return (IReadOnlyList<decimal>)kv.Value;
                });
        }
    }
}
