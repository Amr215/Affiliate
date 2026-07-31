using Affiliate.Data;
using Affiliate.Models;
using Affiliate.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Controllers
{
    public class ProductsController : Controller
    {
        private static readonly HashSet<string> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "Name", "Asin", "CurrentPrice", "LowestPrice", "HighestPrice",
            "DropPercent",
            "Rating", "ReviewsCount", "LastCheckedAt", "NotAvailableDate",
            "CreatedAt", "Position", "Manufacturer"
        };

        private readonly AffiliateDbContext _context;

        public ProductsController(AffiliateDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index([FromQuery] ProductFilterViewModel filter, CancellationToken cancellationToken)
        {
            NormalizeFilter(filter);

            var query = ApplyFilters(_context.Products.AsNoTracking(), filter);
            var totalCount = await query.CountAsync(cancellationToken);

            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)filter.PageSize));
            if (filter.Page > totalPages)
                filter.Page = totalPages;

            var products = await ApplySort(query.Include(p => p.ScraperSearch), filter.SortBy, filter.SortDir)
                .Skip((filter.Page - 1) * filter.PageSize)
                .Take(filter.PageSize)
                .ToListAsync(cancellationToken);

            var model = new ProductsIndexViewModel
            {
                Filter = filter,
                Products = products,
                TotalCount = totalCount,
                TotalPages = totalPages,
                Manufacturers = await _context.Products.AsNoTracking()
                    .Where(p => p.Manufacturer != null && p.Manufacturer != "")
                    .Select(p => p.Manufacturer!)
                    .Distinct()
                    .OrderBy(m => m)
                    .Take(200)
                    .ToListAsync(cancellationToken),
                Currencies = await _context.Products.AsNoTracking()
                    .Where(p => p.Currency != null && p.Currency != "")
                    .Select(p => p.Currency!)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToListAsync(cancellationToken),
                Statuses = await _context.Products.AsNoTracking()
                    .Where(p => p.Status != null && p.Status != "")
                    .Select(p => p.Status!)
                    .Distinct()
                    .OrderBy(s => s)
                    .ToListAsync(cancellationToken),
                Searches = await _context.ScraperSearches.AsNoTracking()
                    .OrderBy(s => s.Name)
                    .Select(s => new ScraperSearchOption { Id = s.Id, Name = s.Name, Query = s.Query })
                    .ToListAsync(cancellationToken)
            };

            ViewBag.SortByOptions = new SelectList(SortOptions, nameof(SelectListItem.Value), nameof(SelectListItem.Text), filter.SortBy);
            return View(model);
        }

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
            new("ASIN", "Asin"),
            new("الشركة المصنّعة", "Manufacturer"),
            new("الترتيب", "Position")
        ];

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkUpdateAvailability(
            [FromBody] BulkUpdateAvailabilityRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Ids is not { Length: > 0 })
                return BadRequest(new { success = false, message = "لم يتم تحديد أي منتجات." });

            var products = await _context.Products
                .Where(p => request.Ids.Contains(p.Id))
                .ToListAsync(cancellationToken);

            if (products.Count == 0)
                return BadRequest(new { success = false, message = "لم يتم العثور على المنتجات المحددة." });

            var status = request.IsAvailable ? "Available" : "Unavailable";
            var now = DateTime.UtcNow;
            foreach (var product in products)
            {
                product.IsAvailable = request.IsAvailable;
                product.Status = status;
                if (request.IsAvailable)
                    product.NotAvailableDate = null;
                else
                    product.NotAvailableDate ??= now;
            }

            await _context.SaveChangesAsync(cancellationToken);

            var message = request.IsAvailable
                ? $"تم تعيين {products.Count} منتج كمتوفر."
                : $"تم تعيين {products.Count} منتج كغير متوفر.";

            return Ok(new
            {
                success = true,
                message,
                products = products.Select(ToBulkUpdateResult).ToList()
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkUpdateBlocked(
            [FromBody] BulkUpdateBlockedRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Ids is not { Length: > 0 })
                return BadRequest(new { success = false, message = "لم يتم تحديد أي منتجات." });

            var products = await _context.Products
                .Where(p => request.Ids.Contains(p.Id))
                .ToListAsync(cancellationToken);

            if (products.Count == 0)
                return BadRequest(new { success = false, message = "لم يتم العثور على المنتجات المحددة." });

            var now = DateTime.UtcNow;
            foreach (var product in products)
            {
                product.IsBlocked = request.IsBlocked;
                if (request.IsBlocked)
                {
                    product.IsAvailable = false;
                    product.Status = "Unavailable";
                    product.NotAvailableDate ??= now;
                }
            }

            await _context.SaveChangesAsync(cancellationToken);

            var message = request.IsBlocked
                ? $"تم حظر {products.Count} منتج (وتعيينها كغير متوفرة)."
                : $"تم إلغاء حظر {products.Count} منتج.";

            return Ok(new
            {
                success = true,
                message,
                products = products.Select(ToBulkUpdateResult).ToList()
            });
        }

        public async Task<IActionResult> Details(
            int? id,
            DateTime? historyFrom,
            DateTime? historyTo,
            CancellationToken cancellationToken)
        {
            if (id == null)
                return NotFound();

            var product = await _context.Products.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (product == null)
                return NotFound();

            var historyQuery = _context.PriceHistories.AsNoTracking()
                .Where(h => h.ProductId == product.Id);

            if (historyFrom.HasValue)
                historyQuery = historyQuery.Where(h => h.CheckedAt >= historyFrom.Value.Date);

            if (historyTo.HasValue)
                historyQuery = historyQuery.Where(h => h.CheckedAt < historyTo.Value.Date.AddDays(1));

            var history = await historyQuery
                .OrderByDescending(h => h.CheckedAt)
                .ToListAsync(cancellationToken);

            return View(new ProductDetailsViewModel
            {
                Product = product,
                History = history,
                HistoryFrom = historyFrom,
                HistoryTo = historyTo
            });
        }

        private static BulkUpdateProductResult ToBulkUpdateResult(Product product) => new()
        {
            Id = product.Id,
            IsAvailable = product.IsAvailable,
            IsBlocked = product.IsBlocked == true,
            NotAvailableDate = product.NotAvailableDate
        };

        private static void NormalizeFilter(ProductFilterViewModel filter)
        {
            filter.Page = Math.Max(1, filter.Page);
            filter.PageSize = filter.PageSize switch
            {
                10 or 25 or 50 or 100 or 500 => filter.PageSize,
                _ => 100
            };

            if (!AllowedSortColumns.Contains(filter.SortBy))
                filter.SortBy = "DropPercent";

            filter.SortDir = string.Equals(filter.SortDir, "asc", StringComparison.OrdinalIgnoreCase)
                ? "asc"
                : "desc";

            // "أقل سعر" / "أعلى سعر" should match the visible price column (current price).
            if (string.Equals(filter.SortBy, "LowestPrice", StringComparison.OrdinalIgnoreCase))
                filter.SortDir = "asc";
            else if (string.Equals(filter.SortBy, "HighestPrice", StringComparison.OrdinalIgnoreCase))
                filter.SortDir = "desc";

            filter.Search = TrimOrNull(filter.Search);
            filter.Asin = TrimOrNull(filter.Asin);
            filter.Manufacturer = TrimOrNull(filter.Manufacturer);
            filter.Currency = TrimOrNull(filter.Currency);
            filter.Status = TrimOrNull(filter.Status);
        }

        private static string? TrimOrNull(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static IQueryable<Product> ApplyFilters(IQueryable<Product> query, ProductFilterViewModel filter)
        {
            if (filter.Search != null)
            {
                var term = filter.Search;
                query = query.Where(p =>
                    p.Name.Contains(term) ||
                    (p.Asin != null && p.Asin.Contains(term)) ||
                    (p.Manufacturer != null && p.Manufacturer.Contains(term)));
            }

            if (filter.Asin != null)
                query = query.Where(p => p.Asin != null && p.Asin.Contains(filter.Asin));

            if (filter.ScraperSearchId.HasValue)
                query = query.Where(p => p.ScraperSearchId == filter.ScraperSearchId.Value);

            if (filter.Manufacturer != null)
                query = query.Where(p => p.Manufacturer == filter.Manufacturer);

            if (filter.Currency != null)
                query = query.Where(p => p.Currency == filter.Currency);

            if (filter.Status != null)
                query = query.Where(p => p.Status == filter.Status);

            if (filter.IsAvailable.HasValue)
                query = query.Where(p => p.IsAvailable == filter.IsAvailable.Value);

            if (filter.IsBlocked.HasValue)
                query = filter.IsBlocked.Value
                    ? query.Where(p => p.IsBlocked == true)
                    : query.Where(p => p.IsBlocked != true);

            if (filter.HasNotAvailableDate)
                query = query.Where(p => p.NotAvailableDate != null);

            if (filter.IsPrime.HasValue)
                query = query.Where(p => p.IsPrime == filter.IsPrime.Value);

            if (filter.IsSponsored.HasValue)
                query = query.Where(p => p.IsSponsored == filter.IsSponsored.Value);

            if (filter.IsBestSeller.HasValue)
                query = query.Where(p => p.IsBestSeller == filter.IsBestSeller.Value);

            if (filter.MinPrice.HasValue)
                query = query.Where(p => p.CurrentPrice >= filter.MinPrice.Value);

            if (filter.MaxPrice.HasValue)
                query = query.Where(p => p.CurrentPrice <= filter.MaxPrice.Value);

            if (filter.MinDropPercent.HasValue)
                query = query.Where(p => p.DropPercent >= filter.MinDropPercent.Value);

            if (filter.MinRating.HasValue)
                query = query.Where(p => p.Rating >= filter.MinRating.Value);

            if (filter.MinReviews.HasValue)
                query = query.Where(p => p.ReviewsCount >= filter.MinReviews.Value);

            if (filter.LastCheckedFrom.HasValue)
                query = query.Where(p => p.LastCheckedAt >= filter.LastCheckedFrom.Value.Date);

            if (filter.LastCheckedTo.HasValue)
                query = query.Where(p => p.LastCheckedAt < filter.LastCheckedTo.Value.Date.AddDays(1));

            if (filter.CreatedFrom.HasValue)
                query = query.Where(p => p.CreatedAt >= filter.CreatedFrom.Value.Date);

            if (filter.CreatedTo.HasValue)
                query = query.Where(p => p.CreatedAt < filter.CreatedTo.Value.Date.AddDays(1));

            return query;
        }

        private static IQueryable<Product> ApplySort(IQueryable<Product> query, string sortBy, string sortDir)
        {
            var desc = sortDir == "desc";

            // Nullable columns: push nulls last so ASC/DESC look correct on page 1.
            return sortBy.ToLowerInvariant() switch
            {
                "name" => desc
                    ? query.OrderByDescending(p => p.Name).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.Name).ThenBy(p => p.Id),
                "asin" => desc
                    ? query.OrderByDescending(p => p.Asin).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.Asin).ThenBy(p => p.Id),
                "currentprice" => desc
                    ? query.OrderBy(p => p.CurrentPrice == null).ThenByDescending(p => p.CurrentPrice).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.CurrentPrice == null).ThenBy(p => p.CurrentPrice).ThenBy(p => p.Id),
                "lowestprice" => query
                    .OrderBy(p => p.CurrentPrice == null)
                    .ThenBy(p => p.CurrentPrice)
                    .ThenBy(p => p.Id),
                "highestprice" => query
                    .OrderBy(p => p.CurrentPrice == null)
                    .ThenByDescending(p => p.CurrentPrice)
                    .ThenBy(p => p.Id),
                "droppercent" => desc
                    ? query.OrderBy(p => p.DropPercent == null).ThenByDescending(p => p.DropPercent).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.DropPercent == null).ThenBy(p => p.DropPercent).ThenBy(p => p.Id),
                "rating" => desc
                    ? query.OrderBy(p => p.Rating == null).ThenByDescending(p => p.Rating).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.Rating == null).ThenBy(p => p.Rating).ThenBy(p => p.Id),
                "reviewscount" => desc
                    ? query.OrderBy(p => p.ReviewsCount == null).ThenByDescending(p => p.ReviewsCount).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.ReviewsCount == null).ThenBy(p => p.ReviewsCount).ThenBy(p => p.Id),
                "createdat" => desc
                    ? query.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id),
                "notavailabledate" => desc
                    ? query.OrderBy(p => p.NotAvailableDate == null).ThenByDescending(p => p.NotAvailableDate).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.NotAvailableDate == null).ThenBy(p => p.NotAvailableDate).ThenBy(p => p.Id),
                "position" => desc
                    ? query.OrderBy(p => p.Position == null).ThenByDescending(p => p.Position).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.Position == null).ThenBy(p => p.Position).ThenBy(p => p.Id),
                "manufacturer" => desc
                    ? query.OrderByDescending(p => p.Manufacturer).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.Manufacturer).ThenBy(p => p.Id),
                _ => desc
                    ? query.OrderByDescending(p => p.LastCheckedAt).ThenBy(p => p.Id)
                    : query.OrderBy(p => p.LastCheckedAt).ThenBy(p => p.Id)
            };
        }
    }
}
