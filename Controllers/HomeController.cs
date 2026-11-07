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
        /// Public deals: products whose Amazon price is below the admin-set external lowest price,
        /// ordered by discount % = (external − amazon) / external, highest first.
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

            var query = db.Products.AsNoTracking()
                .Where(p => p.IsAvailable && p.IsBlocked != true
                    && p.NotAvailableDate == null
                    && p.Asin != null
                    && p.ExternalLowestPrice > 0
                    && p.CurrentPrice > 0
                    && p.CurrentPrice < p.ExternalLowestPrice);

            if (tagId.HasValue)
                query = query.Where(p => p.ScraperUrl != null && p.ScraperUrl.TagId == tagId.Value);

            var totalCount = await query.CountAsync(cancellationToken);
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)DealsPageSize));
            page = Math.Clamp(page, 1, totalPages);

            var rows = await query
                .OrderByDescending(p => (p.ExternalLowestPrice!.Value - p.CurrentPrice!.Value) / p.ExternalLowestPrice!.Value)
                .ThenBy(p => p.Id)
                .Skip((page - 1) * DealsPageSize)
                .Take(DealsPageSize)
                .Select(p => new { p.Name, p.NameAr, p.ImageUrl, p.CurrentPrice, p.Currency, p.Asin })
                .ToListAsync(cancellationToken);

            var deals = rows
                .Where(r => AsinLinkCodec.IsValidAsin(AsinLinkCodec.NormalizeAsin(r.Asin!)))
                .Select(r => new PublicDealItem
                {
                    Name = r.Name,
                    NameAr = r.NameAr,
                    ImageUrl = r.ImageUrl,
                    Price = r.CurrentPrice!.Value,
                    Currency = r.Currency,
                    Url = "/" + codec.Encode(r.Asin!)
                })
                .ToList();

            return View(new PublicDealsViewModel
            {
                Deals = deals,
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
