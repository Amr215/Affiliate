using Affiliate.Data;
using Affiliate.Noon.Models;
using Affiliate.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Noon.Controllers
{
    public class NoonProductsController(AffiliateDbContext context) : Controller
    {
        private static readonly SelectListItem[] SortOptions =
        [
            new("انخفاض %", "DropPercent"),
            new("السعر الحالي", "CurrentPrice"),
            new("أقل سعر", "LowestPrice"),
            new("أعلى سعر", "HighestPrice"),
            new("التقييم", "Rating"),
            new("عدد المراجعات", "ReviewsCount"),
            new("آخر فحص", "LastCheckedAt"),
            new("تاريخ عدم التوفر", "NotAvailableDate"),
            new("تاريخ الإنشاء", "CreatedAt"),
            new("الاسم", "Name"),
            new("SKU", "Sku")
        ];

        public async Task<IActionResult> Index([FromQuery] NoonProductFilterViewModel filter, CancellationToken ct)
        {
            NormalizeFilter(filter);

            var query = ApplyFilters(context.NoonProducts.AsNoTracking(), filter);
            var totalCount = await query.CountAsync(ct);
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)filter.PageSize));
            filter.Page = Math.Min(filter.Page, totalPages);

            var products = await ApplySort(query.Include(p => p.NoonBrandUrl), filter.SortBy, filter.SortDir)
                .Skip((filter.Page - 1) * filter.PageSize)
                .Take(filter.PageSize)
                .ToListAsync(ct);

            ViewBag.SortByOptions = new SelectList(SortOptions, nameof(SelectListItem.Value), nameof(SelectListItem.Text), filter.SortBy);
            return View(new NoonProductsIndexViewModel
            {
                Filter = filter,
                Products = products,
                TotalCount = totalCount,
                TotalPages = totalPages,
                Brands = await context.NoonBrandUrls.AsNoTracking().OrderBy(b => b.Name).ToListAsync(ct),
                Stores = await context.NoonProducts.AsNoTracking()
                    .Where(p => p.StoreName != null && p.StoreName != "")
                    .Select(p => p.StoreName!)
                    .Distinct()
                    .OrderBy(s => s)
                    .Take(200)
                    .ToListAsync(ct)
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkUpdateAvailability([FromBody] BulkUpdateAvailabilityRequest request, CancellationToken ct)
        {
            var products = await LoadSelectedAsync(request.Ids, ct);
            if (products.Count == 0)
                return BadRequest(new { success = false, message = "لم يتم تحديد أي منتجات." });

            var now = DateTime.UtcNow;
            foreach (var product in products)
            {
                product.IsAvailable = request.IsAvailable;
                product.NotAvailableDate = request.IsAvailable ? null : product.NotAvailableDate ?? now;
            }

            await context.SaveChangesAsync(ct);
            return BulkResult(products, request.IsAvailable
                ? $"تم تعيين {products.Count} منتج كمتوفر."
                : $"تم تعيين {products.Count} منتج كغير متوفر.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkUpdateBlocked([FromBody] BulkUpdateBlockedRequest request, CancellationToken ct)
        {
            var products = await LoadSelectedAsync(request.Ids, ct);
            if (products.Count == 0)
                return BadRequest(new { success = false, message = "لم يتم تحديد أي منتجات." });

            var now = DateTime.UtcNow;
            foreach (var product in products)
            {
                product.IsBlocked = request.IsBlocked;
                if (request.IsBlocked)
                {
                    product.IsAvailable = false;
                    product.NotAvailableDate ??= now;
                }
            }

            await context.SaveChangesAsync(ct);
            return BulkResult(products, request.IsBlocked
                ? $"تم حظر {products.Count} منتج (وتعيينها كغير متوفرة)."
                : $"تم إلغاء حظر {products.Count} منتج.");
        }

        public async Task<IActionResult> Details(int id, DateTime? historyFrom, DateTime? historyTo, CancellationToken ct)
        {
            var product = await context.NoonProducts.AsNoTracking()
                .Include(p => p.NoonBrandUrl)
                .FirstOrDefaultAsync(p => p.Id == id, ct);
            if (product is null)
                return NotFound();

            var history = context.NoonPriceHistories.AsNoTracking().Where(h => h.NoonProductId == id);
            if (historyFrom.HasValue)
                history = history.Where(h => h.CheckedAt >= historyFrom.Value.Date);
            if (historyTo.HasValue)
                history = history.Where(h => h.CheckedAt < historyTo.Value.Date.AddDays(1));

            return View(new NoonProductDetailsViewModel
            {
                Product = product,
                History = await history.OrderByDescending(h => h.CheckedAt).ToListAsync(ct),
                HistoryFrom = historyFrom,
                HistoryTo = historyTo
            });
        }

        private async Task<List<NoonProduct>> LoadSelectedAsync(int[]? ids, CancellationToken ct) =>
            ids is { Length: > 0 }
                ? await context.NoonProducts.Where(p => ids.Contains(p.Id)).ToListAsync(ct)
                : [];

        private OkObjectResult BulkResult(List<NoonProduct> products, string message) => Ok(new
        {
            success = true,
            message,
            products = products.Select(p => new BulkUpdateProductResult
            {
                Id = p.Id,
                IsAvailable = p.IsAvailable,
                IsBlocked = p.IsBlocked,
                NotAvailableDate = p.NotAvailableDate
            }).ToList()
        });

        private static void NormalizeFilter(NoonProductFilterViewModel filter)
        {
            filter.Page = Math.Max(1, filter.Page);
            filter.PageSize = filter.PageSize is 10 or 25 or 50 or 100 or 500 ? filter.PageSize : 100;

            if (SortOptions.All(o => !string.Equals(o.Value, filter.SortBy, StringComparison.OrdinalIgnoreCase)))
                filter.SortBy = "DropPercent";

            filter.SortDir = string.Equals(filter.SortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";

            // "أقل سعر" / "أعلى سعر" sort by the visible current price.
            if (string.Equals(filter.SortBy, "LowestPrice", StringComparison.OrdinalIgnoreCase))
                filter.SortDir = "asc";
            else if (string.Equals(filter.SortBy, "HighestPrice", StringComparison.OrdinalIgnoreCase))
                filter.SortDir = "desc";

            filter.Search = TrimOrNull(filter.Search);
            filter.Sku = TrimOrNull(filter.Sku);
            filter.StoreName = TrimOrNull(filter.StoreName);
        }

        private static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static IQueryable<NoonProduct> ApplyFilters(IQueryable<NoonProduct> query, NoonProductFilterViewModel f)
        {
            if (f.Search != null)
                query = query.Where(p => p.Name.Contains(f.Search) || p.Sku.Contains(f.Search) || (p.Brand != null && p.Brand.Contains(f.Search)));
            if (f.Sku != null)
                query = query.Where(p => p.Sku.Contains(f.Sku));
            if (f.BrandId.HasValue)
                query = query.Where(p => p.NoonBrandUrlId == f.BrandId);
            if (f.StoreName != null)
                query = query.Where(p => p.StoreName == f.StoreName);
            if (f.IsAvailable.HasValue)
                query = query.Where(p => p.IsAvailable == f.IsAvailable);
            if (f.IsBlocked.HasValue)
                query = query.Where(p => p.IsBlocked == f.IsBlocked);
            if (f.HasNotAvailableDate)
                query = query.Where(p => p.NotAvailableDate != null);
            if (f.IsBestSeller.HasValue)
                query = query.Where(p => p.IsBestSeller == f.IsBestSeller);
            if (f.MinPrice.HasValue)
                query = query.Where(p => p.CurrentPrice >= f.MinPrice);
            if (f.MaxPrice.HasValue)
                query = query.Where(p => p.CurrentPrice <= f.MaxPrice);
            if (f.MinDropPercent.HasValue)
                query = query.Where(p => p.DropPercent >= f.MinDropPercent);
            if (f.MinRating.HasValue)
                query = query.Where(p => p.Rating >= f.MinRating);
            if (f.MinReviews.HasValue)
                query = query.Where(p => p.ReviewsCount >= f.MinReviews);
            if (f.LastCheckedFrom.HasValue)
                query = query.Where(p => p.LastCheckedAt >= f.LastCheckedFrom.Value.Date);
            if (f.LastCheckedTo.HasValue)
                query = query.Where(p => p.LastCheckedAt < f.LastCheckedTo.Value.Date.AddDays(1));
            if (f.CreatedFrom.HasValue)
                query = query.Where(p => p.CreatedAt >= f.CreatedFrom.Value.Date);
            if (f.CreatedTo.HasValue)
                query = query.Where(p => p.CreatedAt < f.CreatedTo.Value.Date.AddDays(1));
            return query;
        }

        private static IQueryable<NoonProduct> ApplySort(IQueryable<NoonProduct> query, string sortBy, string sortDir)
        {
            var desc = sortDir == "desc";

            // Nullable columns: nulls last in both directions.
            return sortBy.ToLowerInvariant() switch
            {
                "name" => desc ? query.OrderByDescending(p => p.Name).ThenBy(p => p.Id) : query.OrderBy(p => p.Name).ThenBy(p => p.Id),
                "sku" => desc ? query.OrderByDescending(p => p.Sku) : query.OrderBy(p => p.Sku),
                "currentprice" => desc
                    ? query.OrderBy(p => p.CurrentPrice == null).ThenByDescending(p => p.CurrentPrice).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.CurrentPrice == null).ThenBy(p => p.CurrentPrice).ThenBy(p => p.Id),
                "lowestprice" => query.OrderBy(p => p.CurrentPrice == null).ThenBy(p => p.CurrentPrice).ThenBy(p => p.Id),
                "highestprice" => query.OrderBy(p => p.CurrentPrice == null).ThenByDescending(p => p.CurrentPrice).ThenBy(p => p.Id),
                "droppercent" => desc
                    ? query.OrderBy(p => p.DropPercent == null).ThenByDescending(p => p.DropPercent).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.DropPercent == null).ThenBy(p => p.DropPercent).ThenBy(p => p.Id),
                "rating" => desc
                    ? query.OrderBy(p => p.Rating == null).ThenByDescending(p => p.Rating).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.Rating == null).ThenBy(p => p.Rating).ThenBy(p => p.Id),
                "reviewscount" => desc
                    ? query.OrderBy(p => p.ReviewsCount == null).ThenByDescending(p => p.ReviewsCount).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.ReviewsCount == null).ThenBy(p => p.ReviewsCount).ThenBy(p => p.Id),
                "notavailabledate" => desc
                    ? query.OrderBy(p => p.NotAvailableDate == null).ThenByDescending(p => p.NotAvailableDate).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.NotAvailableDate == null).ThenBy(p => p.NotAvailableDate).ThenBy(p => p.Id),
                "createdat" => desc ? query.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id) : query.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id),
                _ => desc ? query.OrderByDescending(p => p.LastCheckedAt).ThenBy(p => p.Id) : query.OrderBy(p => p.LastCheckedAt).ThenBy(p => p.Id)
            };
        }
    }
}
