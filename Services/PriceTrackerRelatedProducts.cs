using Affiliate.Data;
using Affiliate.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Services
{
    /// <summary>
    /// Other tracker-visible products from the same <see cref="Affiliate.Models.ScraperUrl"/>
    /// (same eligibility as the product details page, not the homepage list).
    /// </summary>
    public static class PriceTrackerRelatedProducts
    {
        public const int DefaultTake = 12;

        public static async Task<IReadOnlyList<PublicDealItem>> LoadTopByScraperAsync(
            AffiliateDbContext db,
            int scraperUrlId,
            int excludeProductId,
            string normalizedLang,
            int take = DefaultTake,
            CancellationToken cancellationToken = default)
        {
            take = Math.Clamp(take, 1, 48);
            var isArabic = string.Equals(normalizedLang, "ar", StringComparison.OrdinalIgnoreCase);

            var candidates = await db.Products.AsNoTracking()
                .Where(p => p.Id != excludeProductId
                    && p.IsBlocked != true
                    && p.Asin != null
                    && p.ScraperUrlId == scraperUrlId
                    && p.ScraperUrl != null
                    && p.ScraperUrl.IsAsinRecheckEnabled
                    && p.NotAvailableDate == null
                    && p.CurrentPrice > 0)
                .Select(p => new Row(
                    p.Id,
                    p.Name,
                    p.NameAr,
                    p.ImageUrl,
                    p.CurrentPrice,
                    p.LowestPrice,
                    p.Currency,
                    p.Asin!))
                .ToListAsync(cancellationToken);

            candidates = candidates
                .Where(r => AsinLinkCodec.IsValidAsin(AsinLinkCodec.NormalizeAsin(r.Asin)))
                .ToList();

            if (candidates.Count == 0)
                return [];

            var ids = candidates.Select(c => c.Id).ToList();
            var historyByProduct = new Dictionary<int, List<(DateTime CheckedAt, decimal Price)>>();
            const int chunkSize = 500;
            for (var offset = 0; offset < ids.Count; offset += chunkSize)
            {
                var chunk = ids.Skip(offset).Take(chunkSize).ToList();
                var histRows = await db.PriceHistories.AsNoTracking()
                    .Where(h => chunk.Contains(h.ProductId) && h.Price != null && h.Price > 0)
                    .Select(h => new { h.ProductId, h.CheckedAt, Price = h.Price!.Value })
                    .ToListAsync(cancellationToken);

                foreach (var row in histRows)
                {
                    if (!historyByProduct.TryGetValue(row.ProductId, out var list))
                    {
                        list = [];
                        historyByProduct[row.ProductId] = list;
                    }
                    list.Add((row.CheckedAt, row.Price));
                }
            }

            foreach (var list in historyByProduct.Values)
                list.Sort((a, b) => a.CheckedAt.CompareTo(b.CheckedAt));

            var ranked = candidates
                .Select(r =>
                {
                    var current = r.CurrentPrice!.Value;
                    historyByProduct.TryGetValue(r.Id, out var hist);
                    hist ??= [];
                    var histPrices = hist.Select(p => p.Price).ToList();
                    var (tier, rank) = PriceTrackerSort.Compute(current, histPrices, r.LowestPrice);
                    return (Row: r, Tier: tier, Rank: rank);
                })
                .OrderBy(x => x.Tier)
                .ThenByDescending(x => x.Tier <= PriceTrackerSort.TierAllTimeLow ? x.Rank : 0m)
                .ThenBy(x => x.Tier == PriceTrackerSort.TierClosestToAllTimeLow ? x.Rank : 0m)
                .ThenBy(x => x.Row.Id)
                .Take(take)
                .ToList();

            var items = ranked
                .Select(x =>
                {
                    var r = x.Row;
                    var asin = AsinLinkCodec.NormalizeAsin(r.Asin);
                    var display = isArabic && !string.IsNullOrWhiteSpace(r.NameAr) ? r.NameAr! : r.Name;
                    var slug = PriceTrackerSlug.FromName(display);
                    return new PublicDealItem
                    {
                        ProductId = r.Id,
                        Name = r.Name,
                        NameAr = r.NameAr,
                        ImageUrl = r.ImageUrl,
                        Price = r.CurrentPrice!.Value,
                        Currency = r.Currency,
                        Url = $"/tracker/{asin}/{Uri.EscapeDataString(slug)}?lang={normalizedLang}",
                        IsDeal = false,
                        IsAtLowestPrice = x.Tier <= PriceTrackerSort.TierAllTimeLow
                    };
                })
                .ToList();

            foreach (var item in items)
            {
                if (historyByProduct.TryGetValue(item.ProductId, out var hist) && hist.Count > 0)
                    item.LowestPriceDays = PriceTrackerStats.LowestStreak(hist).LowestPriceDays;
            }

            return items;
        }

        private sealed record Row(
            int Id,
            string Name,
            string? NameAr,
            string? ImageUrl,
            decimal? CurrentPrice,
            decimal? LowestPrice,
            string? Currency,
            string Asin);
    }
}
