using Affiliate.Data;
using Affiliate.Options;
using Affiliate.Services;
using Affiliate.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Affiliate.Controllers
{
    [AllowAnonymous]
    public class PriceTrackerController : Controller
    {
        private const int PageSize = 24;

        private readonly AffiliateDbContext _db;
        private readonly AsinLinkCodec _codec;
        private readonly string _siteBase;

        public PriceTrackerController(
            AffiliateDbContext db,
            AsinLinkCodec codec,
            IOptions<TelegramOptions> telegramOptions)
        {
            _db = db;
            _codec = codec;
            _siteBase = (telegramOptions.Value.PublishLinkBaseUrl ?? "https://el-sindbad.com").Trim().TrimEnd('/');
        }

        [HttpGet("/tracker")]
        public async Task<IActionResult> Index(
            string? q = null,
            int? tagId = null,
            string? lang = null,
            int page = 1,
            CancellationToken cancellationToken = default)
        {
            var normalizedLang = NormalizeLang(lang);
            var isArabic = normalizedLang == "ar";
            if (tagId is <= 0)
                tagId = null;

            var queryText = (q ?? string.Empty).Trim();
            if (queryText.Length > 120)
                queryText = queryText[..120];

            var tagsBase = _db.SearchTags.AsNoTracking();
            var tagsOrdered = isArabic
                ? tagsBase.OrderBy(t => t.NameAr).ThenBy(t => t.NameEn)
                : tagsBase.OrderBy(t => t.NameEn).ThenBy(t => t.NameAr);
            var tags = await tagsOrdered
                .Select(t => new PublicDealTagOption { Id = t.Id, NameEn = t.NameEn, NameAr = t.NameAr })
                .ToListAsync(cancellationToken);

            if (tagId.HasValue && tags.All(t => t.Id != tagId.Value))
                tagId = null;

            var products = TrackableProducts();

            if (tagId.HasValue)
                products = products.Where(p => p.ScraperUrl!.TagId == tagId.Value);

            if (!string.IsNullOrEmpty(queryText))
            {
                var like = $"%{EscapeLike(queryText)}%";
                products = products.Where(p =>
                    EF.Functions.Like(p.Name, like)
                    || (p.NameAr != null && EF.Functions.Like(p.NameAr, like))
                    || (p.Asin != null && EF.Functions.Like(p.Asin, like)));
            }

            var candidates = await products
                .Select(p => new
                {
                    p.Id,
                    p.Asin,
                    p.Name,
                    p.NameAr,
                    p.ImageUrl,
                    p.CurrentPrice,
                    p.LowestPrice,
                    p.HighestPrice,
                    p.Currency
                })
                .ToListAsync(cancellationToken);

            candidates = candidates
                .Where(r => AsinLinkCodec.IsValidAsin(AsinLinkCodec.NormalizeAsin(r.Asin!)))
                .ToList();

            var productIds = candidates.Select(c => c.Id).ToList();
            var historyByProduct = new Dictionary<int, List<decimal>>();
            if (productIds.Count > 0)
            {
                // Load in chunks to keep the SQL IN clause manageable.
                const int chunkSize = 500;
                for (var offset = 0; offset < productIds.Count; offset += chunkSize)
                {
                    var chunk = productIds.Skip(offset).Take(chunkSize).ToList();
                    var histRows = await _db.PriceHistories.AsNoTracking()
                        .Where(h => chunk.Contains(h.ProductId) && h.Price != null && h.Price > 0)
                        .Select(h => new { h.ProductId, Price = h.Price!.Value })
                        .ToListAsync(cancellationToken);

                    foreach (var row in histRows)
                    {
                        if (!historyByProduct.TryGetValue(row.ProductId, out var list))
                        {
                            list = [];
                            historyByProduct[row.ProductId] = list;
                        }
                        list.Add(row.Price);
                    }
                }
            }

            var ranked = candidates
                .Select(r =>
                {
                    var current = r.CurrentPrice ?? 0m;
                    historyByProduct.TryGetValue(r.Id, out var hist);
                    hist ??= [];
                    var (tier, rank) = PriceTrackerSort.Compute(current, hist, r.LowestPrice);
                    return (Row: r, Tier: tier, Rank: rank);
                })
                .OrderBy(x => x.Tier)
                .ThenByDescending(x => x.Tier <= PriceTrackerSort.TierAllTimeLow ? x.Rank : 0m)
                .ThenBy(x => x.Tier == PriceTrackerSort.TierClosestToAllTimeLow ? x.Rank : 0m)
                .ThenBy(x => x.Row.Id)
                .ToList();

            var totalCount = ranked.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
            page = Math.Clamp(page, 1, totalPages);

            var items = ranked
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .Select(x =>
                {
                    var r = x.Row;
                    var asin = AsinLinkCodec.NormalizeAsin(r.Asin!);
                    var atLow = x.Tier <= PriceTrackerSort.TierAllTimeLow;
                    var display = isArabic && !string.IsNullOrWhiteSpace(r.NameAr) ? r.NameAr! : r.Name;
                    return new PriceTrackerListItem
                    {
                        Asin = asin,
                        Name = r.Name,
                        NameAr = r.NameAr,
                        ImageUrl = r.ImageUrl,
                        CurrentPrice = r.CurrentPrice,
                        LowestPrice = r.LowestPrice,
                        HighestPrice = r.HighestPrice,
                        Currency = r.Currency,
                        Slug = PriceTrackerSlug.FromName(display),
                        IsLowestEver = atLow,
                        IsLowestIn30DaysOrMore = atLow,
                        LowestPriceDays = atLow ? 30 : 0
                    };
                })
                .ToList();

            ViewData["SiteBaseUrl"] = _siteBase;
            return View(new PriceTrackerIndexViewModel
            {
                Lang = normalizedLang,
                Query = queryText,
                SelectedTagId = tagId,
                Tags = tags,
                Page = page,
                TotalPages = totalPages,
                TotalCount = totalCount,
                Items = items
            });
        }

        [HttpGet("/tracker/{asin}/{slug?}")]
        public async Task<IActionResult> Product(
            string asin,
            string? slug = null,
            string? lang = null,
            CancellationToken cancellationToken = default)
        {
            var normalizedLang = NormalizeLang(lang);
            asin = AsinLinkCodec.NormalizeAsin(asin);
            if (!AsinLinkCodec.IsValidAsin(asin))
                return NotFound();

            var anyProduct = await _db.Products.AsNoTracking()
                .Where(p => p.Asin == asin)
                .Select(p => new
                {
                    p.Id,
                    p.Asin,
                    p.Name,
                    p.NameAr,
                    p.ImageUrl,
                    p.CurrentPrice,
                    p.LowestPrice,
                    p.HighestPrice,
                    p.Currency,
                    p.IsBlocked,
                    p.NotAvailableDate,
                    HasTag = p.ScraperUrl != null && p.ScraperUrl.TagId != null
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (anyProduct is null)
                return NotFound();

            var isTrackable = anyProduct.IsBlocked != true
                && anyProduct.NotAvailableDate == null
                && anyProduct.CurrentPrice > 0
                && anyProduct.HasTag;

            if (!isTrackable)
            {
                Response.StatusCode = StatusCodes.Status410Gone;
                ViewData["SiteBaseUrl"] = _siteBase;
                ViewData["Robots"] = "noindex, follow";
                return View("Unavailable", new PriceTrackerUnavailableViewModel
                {
                    Lang = normalizedLang,
                    Asin = asin,
                    Name = anyProduct.Name,
                    NameAr = anyProduct.NameAr,
                    ImageUrl = anyProduct.ImageUrl
                });
            }

            var product = anyProduct;

            var displayName = normalizedLang == "ar" && !string.IsNullOrWhiteSpace(product.NameAr)
                ? product.NameAr!
                : product.Name;
            var canonicalSlug = PriceTrackerSlug.FromName(displayName);

            if (!string.Equals(slug, canonicalSlug, StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToActionPermanent(nameof(Product), new
                {
                    asin,
                    slug = canonicalSlug,
                    lang = normalizedLang
                });
            }

            var history = await _db.PriceHistories.AsNoTracking()
                .Where(h => h.ProductId == product.Id && h.Price != null && h.Price > 0)
                .OrderByDescending(h => h.CheckedAt)
                .Take(200)
                .Select(h => new { h.CheckedAt, Price = h.Price!.Value })
                .ToListAsync(cancellationToken);
            history.Reverse();

            var points = history
                .Select(h => (h.CheckedAt, h.Price))
                .ToList();

            // Ensure the live current price is represented on the chart.
            if (product.CurrentPrice is > 0)
            {
                var now = DateTime.UtcNow;
                if (points.Count == 0
                    || points[^1].Price != product.CurrentPrice.Value
                    || (now - points[^1].CheckedAt).TotalHours > 12)
                {
                    points.Add((now, product.CurrentPrice.Value));
                }
            }

            // Need at least 2 points for a line — seed from stored high/low when history is thin.
            if (points.Count == 1)
            {
                var only = points[0];
                var older = product.HighestPrice is > 0 && product.HighestPrice != only.Price
                    ? product.HighestPrice.Value
                    : product.LowestPrice is > 0 && product.LowestPrice != only.Price
                        ? product.LowestPrice.Value
                        : only.Price;
                points.Insert(0, (only.CheckedAt.AddDays(-7), older));
            }

            var stats = PriceTrackerStats.FromHistory(
                product.CurrentPrice,
                product.LowestPrice,
                product.HighestPrice,
                points);

            string buyUrl;
            try
            {
                buyUrl = "/" + _codec.Encode(asin);
            }
            catch
            {
                buyUrl = $"https://www.amazon.eg/dp/{asin}";
            }

            ViewData["SiteBaseUrl"] = _siteBase;
            return View(new PriceTrackerProductViewModel
            {
                Lang = normalizedLang,
                Asin = asin,
                Name = product.Name,
                NameAr = product.NameAr,
                ImageUrl = product.ImageUrl,
                Currency = product.Currency,
                Slug = canonicalSlug,
                BuyUrl = buyUrl,
                Stats = new PriceTrackerStatsSnapshot
                {
                    CurrentPrice = stats.CurrentPrice,
                    LowestPrice = stats.LowestPrice,
                    HighestPrice = stats.HighestPrice,
                    AveragePrice = stats.AveragePrice,
                    LowestPriceDays = stats.LowestPriceDays,
                    IsLowestEver = stats.IsLowestEver,
                    IsLowestIn30DaysOrMore = stats.IsLowestIn30DaysOrMore,
                    ChartPoints = stats.ChartPoints.Select(p => new PriceTrackerChartPoint
                    {
                        Label = p.CheckedAt.ToString("yyyy-MM-dd"),
                        IsoDate = p.CheckedAt.ToString("o"),
                        Price = p.Price
                    }).ToList()
                }
            });
        }

        private IQueryable<Models.Product> TrackableProducts() =>
            _db.Products.AsNoTracking()
                .Where(p => p.IsBlocked != true
                    && p.NotAvailableDate == null
                    && p.Asin != null
                    && p.CurrentPrice > 0
                    && p.ScraperUrl != null
                    && p.ScraperUrl.TagId != null);

        private static string NormalizeLang(string? lang) =>
            string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";

        private static string EscapeLike(string value) =>
            value
                .Replace("[", "[[]", StringComparison.Ordinal)
                .Replace("%", "[%]", StringComparison.Ordinal)
                .Replace("_", "[_]", StringComparison.Ordinal);
    }
}
