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
        private readonly AffiliateDbContext _db;
        private readonly AsinLinkCodec _codec;
        private readonly TelegramOptions _telegramOptions;
        private readonly string _siteBase;

        public PriceTrackerController(
            AffiliateDbContext db,
            AsinLinkCodec codec,
            IOptions<TelegramOptions> telegramOptions)
        {
            _db = db;
            _codec = codec;
            _telegramOptions = telegramOptions.Value;
            _siteBase = (_telegramOptions.PublishLinkBaseUrl ?? "https://el-sindbad.com").Trim().TrimEnd('/');
        }

        /// <summary>List moved to the deals homepage; keep /tracker as a permanent redirect.</summary>
        [HttpGet("/tracker")]
        public IActionResult Index(string? lang = null, string? q = null, int? tagId = null)
        {
            return RedirectToActionPermanent(
                nameof(HomeController.Index),
                "Home",
                new
                {
                    lang = NormalizeLang(lang),
                    q,
                    tagId
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
                    p.DescriptionAr,
                    p.DescriptionEn,
                    p.ImageUrl,
                    p.Rating,
                    p.ReviewsCount,
                    p.CurrentPrice,
                    p.LowestPrice,
                    p.HighestPrice,
                    p.Currency,
                    p.IsBlocked,
                    p.NotAvailableDate,
                    p.ScraperUrlId,
                    HasTag = p.ScraperUrl != null && p.ScraperUrl.TagId != null
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (anyProduct is null)
                return NotFound();

            // NotAvailableDate no longer blocks the page — CurrentPrice is still the last known price.
            var showLastDetected = anyProduct.NotAvailableDate != null;
            var canShow = anyProduct.IsBlocked != true
                && anyProduct.HasTag
                && anyProduct.CurrentPrice > 0;

            if (!canShow)
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

            // Live "now" point only while still actively tracked.
            if (!showLastDetected && product.CurrentPrice is > 0)
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
                buyUrl = "/d/" + _codec.Encode(asin);
            }
            catch
            {
                var dealsTag = _telegramOptions.DealsAffiliateTag?.Trim();
                if (string.IsNullOrEmpty(dealsTag))
                    dealsTag = _telegramOptions.AffiliateTag?.Trim();
                buyUrl = string.IsNullOrEmpty(dealsTag)
                    ? $"https://www.amazon.eg/dp/{asin}"
                    : $"https://www.amazon.eg/dp/{asin}?tag={Uri.EscapeDataString(dealsTag)}";
            }

            IReadOnlyList<PublicDealItem> related = [];
            if (product.ScraperUrlId is > 0)
            {
                related = await PriceTrackerRelatedProducts.LoadTopByScraperAsync(
                    _db,
                    product.ScraperUrlId.Value,
                    product.Id,
                    normalizedLang,
                    cancellationToken: cancellationToken);
            }

            ViewData["SiteBaseUrl"] = _siteBase;
            return View(new PriceTrackerProductViewModel
            {
                Lang = normalizedLang,
                Asin = asin,
                Name = product.Name,
                NameAr = product.NameAr,
                DescriptionAr = product.DescriptionAr,
                DescriptionEn = product.DescriptionEn,
                ImageUrl = product.ImageUrl,
                Rating = product.Rating,
                ReviewsCount = product.ReviewsCount,
                Currency = product.Currency,
                Slug = canonicalSlug,
                BuyUrl = buyUrl,
                ShowLastDetected = showLastDetected,
                LastDetectedAt = showLastDetected
                    ? (points.Count > 0 ? points[^1].CheckedAt : product.NotAvailableDate)
                    : null,
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
                },
                RelatedProducts = related
            });
        }

        private static string NormalizeLang(string? lang) =>
            string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";
    }
}
