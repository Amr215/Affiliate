using System.Diagnostics;
using Affiliate.Data;
using Affiliate.Models;
using Affiliate.Noon.Models;
using Affiliate.Services;
using Affiliate.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Controllers
{
    public class HomeController : Controller
    {
        private const int DealsPageSize = 24;

        private readonly ILogger<HomeController> _logger;

        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Public homepage feed in one grid:
        /// 1) external-price deals (highest discount % first),
        /// 2) then tracker products (same eligibility/sort as /tracker), excluding deal ASINs.
        /// </summary>
        [AllowAnonymous]
        public async Task<IActionResult> Index(
            [FromServices] AffiliateDbContext db,
            [FromServices] AsinLinkCodec codec,
            int? tagId = null,
            string? lang = null,
            int page = 1,
            CancellationToken cancellationToken = default)
        {
            if (tagId is <= 0)
                tagId = null;

            // Arabic is the default; English only when explicitly requested.
            var isArabic = !string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
            var normalizedLang = isArabic ? "ar" : "en";

            var tagsBase = db.SearchTags.AsNoTracking();
            var tagsOrdered = isArabic
                ? tagsBase.OrderBy(t => t.NameAr).ThenBy(t => t.NameEn)
                : tagsBase.OrderBy(t => t.NameEn).ThenBy(t => t.NameAr);
            var tags = await tagsOrdered
                .Select(t => new PublicDealTagOption { Id = t.Id, NameEn = t.NameEn, NameAr = t.NameAr })
                .ToListAsync(cancellationToken);

            if (tagId.HasValue && tags.All(t => t.Id != tagId.Value))
                tagId = null;

            var dealsQuery = db.Products.AsNoTracking()
                .Where(p => p.IsAvailable && p.IsBlocked != true
                    && p.NotAvailableDate == null
                    && p.Asin != null
                    && p.ExternalLowestPrice > 0
                    && p.CurrentPrice > 0
                    && p.CurrentPrice < p.ExternalLowestPrice);

            if (tagId.HasValue)
                dealsQuery = dealsQuery.Where(p => p.ScraperUrl != null && p.ScraperUrl.TagId == tagId.Value);

            var dealRows = await dealsQuery
                .OrderByDescending(p => (p.ExternalLowestPrice!.Value - p.CurrentPrice!.Value) / p.ExternalLowestPrice!.Value)
                .ThenBy(p => p.Id)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.NameAr,
                    p.ImageUrl,
                    p.CurrentPrice,
                    p.LowestPrice,
                    p.Currency,
                    p.Asin
                })
                .ToListAsync(cancellationToken);

            var dealItems = new List<PublicDealItem>();
            var dealIds = new HashSet<int>();
            foreach (var r in dealRows)
            {
                var asin = AsinLinkCodec.NormalizeAsin(r.Asin!);
                if (!AsinLinkCodec.IsValidAsin(asin))
                    continue;

                dealIds.Add(r.Id);
                string url;
                try { url = "/" + codec.Encode(asin); }
                catch { url = $"https://www.amazon.eg/dp/{asin}"; }

                dealItems.Add(new PublicDealItem
                {
                    Name = r.Name,
                    NameAr = r.NameAr,
                    ImageUrl = r.ImageUrl,
                    Price = r.CurrentPrice!.Value,
                    Currency = r.Currency,
                    Url = url,
                    IsDeal = true,
                    IsAtLowestPrice = r.CurrentPrice is > 0
                        && r.LowestPrice is > 0
                        && r.CurrentPrice.Value <= r.LowestPrice.Value
                });
            }

            var trackerQuery = db.Products.AsNoTracking()
                .Where(p => p.IsBlocked != true
                    && p.NotAvailableDate == null
                    && p.Asin != null
                    && p.CurrentPrice > 0
                    && p.ScraperUrl != null
                    && p.ScraperUrl.TagId != null);

            if (tagId.HasValue)
                trackerQuery = trackerQuery.Where(p => p.ScraperUrl!.TagId == tagId.Value);

            if (dealIds.Count > 0)
            {
                var excludeIds = dealIds.ToList();
                trackerQuery = trackerQuery.Where(p => !excludeIds.Contains(p.Id));
            }

            var trackerCandidates = await trackerQuery
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.NameAr,
                    p.ImageUrl,
                    p.CurrentPrice,
                    p.LowestPrice,
                    p.Currency,
                    p.Asin
                })
                .ToListAsync(cancellationToken);

            trackerCandidates = trackerCandidates
                .Where(r => AsinLinkCodec.IsValidAsin(AsinLinkCodec.NormalizeAsin(r.Asin!)))
                .ToList();

            var trackerIds = trackerCandidates.Select(c => c.Id).ToList();
            var historyByProduct = new Dictionary<int, List<decimal>>();
            if (trackerIds.Count > 0)
            {
                const int chunkSize = 500;
                for (var offset = 0; offset < trackerIds.Count; offset += chunkSize)
                {
                    var chunk = trackerIds.Skip(offset).Take(chunkSize).ToList();
                    var histRows = await db.PriceHistories.AsNoTracking()
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

            var trackerItems = trackerCandidates
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
                .Select(x =>
                {
                    var r = x.Row;
                    var asin = AsinLinkCodec.NormalizeAsin(r.Asin!);
                    var display = isArabic && !string.IsNullOrWhiteSpace(r.NameAr) ? r.NameAr! : r.Name;
                    var slug = PriceTrackerSlug.FromName(display);
                    return new PublicDealItem
                    {
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

            var combined = dealItems.Concat(trackerItems).ToList();
            var totalCount = combined.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)DealsPageSize));
            page = Math.Clamp(page, 1, totalPages);
            var pageItems = combined
                .Skip((page - 1) * DealsPageSize)
                .Take(DealsPageSize)
                .ToList();

            return View(new PublicDealsViewModel
            {
                Deals = pageItems,
                Tags = tags,
                SelectedTagId = tagId,
                Page = page,
                TotalPages = totalPages,
                TotalCount = totalCount,
                Lang = normalizedLang
            });
        }

        [HttpGet("/Dashboard")]
        public async Task<IActionResult> Dashboard([FromServices] AffiliateDbContext db)
        {
            var since = DateTime.UtcNow.AddHours(-1);
            return View(new HomeDashboardViewModel
            {
                AmazonProducts = await db.Products.CountAsync(),
                AmazonAvailable = await db.Products.CountAsync(p => p.IsAvailable),
                AmazonUrls = await db.ScraperUrls.CountAsync(s => s.IsEnabled),
                NoonProducts = await db.NoonProducts.CountAsync(),
                NoonAvailable = await db.NoonProducts.CountAsync(p => p.IsAvailable),
                NoonBrands = await db.NoonBrandUrls.CountAsync(b => b.IsEnabled),
                NoonRequestsLastHour = await db.NoonRequestLogs.CountAsync(l => l.RequestedAt >= since),
                NoonFailedLastHour = await db.NoonRequestLogs.CountAsync(l => l.RequestedAt >= since && l.Outcome != NoonRequestOutcome.Success),
                Links = await db.ProductLinks.CountAsync()
            });
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [AllowAnonymous]
        public IActionResult About(string? lang = null)
        {
            SetPublicLang(lang);
            ViewData["Title"] = IsArabicLang() ? "من نحن" : "About us";
            return View();
        }

        [AllowAnonymous]
        public IActionResult Terms(string? lang = null)
        {
            SetPublicLang(lang);
            ViewData["Title"] = IsArabicLang() ? "الشروط والأحكام" : "Terms and conditions";
            return View();
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

        private void SetPublicLang(string? lang)
        {
            // Arabic is the default; English only when explicitly requested.
            ViewData["Lang"] = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";
        }

        private bool IsArabicLang() =>
            string.Equals(ViewData["Lang"] as string, "ar", StringComparison.OrdinalIgnoreCase);
    }
}
