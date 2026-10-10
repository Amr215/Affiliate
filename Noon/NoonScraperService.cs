using System.Collections.Concurrent;
using System.Diagnostics;
using Affiliate.Data;
using Affiliate.Noon.Models;
using Affiliate.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Affiliate.Services.PriceDropRules;

namespace Affiliate.Noon
{
    public interface INoonScraperService
    {
        /// <summary>Scrapes every enabled brand, several brands in parallel.</summary>
        Task ProcessCycleAsync(CancellationToken cancellationToken = default);

        /// <summary>Scrapes one brand now. Returns false if it does not exist or is already running.</summary>
        Task<bool> RunBrandNowAsync(int brandId, CancellationToken cancellationToken = default);
    }

    public sealed class NoonScraperService(
        AffiliateDbContext db,
        NoonCatalogClient catalog,
        NoonCycleCache cycles,
        ITelegramNotifier telegram,
        IOptions<NoonOptions> options,
        ILogger<NoonScraperService> logger) : INoonScraperService
    {
        /// <summary>Brands being scraped right now, across the scheduler and run-now requests.</summary>
        private static readonly ConcurrentDictionary<int, byte> RunningBrands = new();

        /// <summary>
        /// Serializes saves: the DbContext is not thread-safe, and two brands may list the same SKU,
        /// which would otherwise race on the unique index.
        /// </summary>
        private static readonly SemaphoreSlim SaveGate = new(1, 1);

        private readonly NoonOptions _options = options.Value;
        private readonly ConcurrentQueue<NoonRequestLog> _pendingLogs = new();
        private int _successPages, _failedPages, _page1Success;

        public async Task ProcessCycleAsync(CancellationToken ct = default)
        {
            var brands = await db.NoonBrandUrls.AsNoTracking()
                .Where(b => b.IsEnabled)
                .OrderBy(b => b.LastRunAt ?? DateTime.MinValue)
                .ToListAsync(ct);

            if (brands.Count == 0)
                return;

            var stopwatch = Stopwatch.StartNew();
            int complete = 0, failed = 0, products = 0;

            await Parallel.ForEachAsync(
                brands,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelBrands), CancellationToken = ct },
                async (brand, token) =>
                {
                    // Spread the first requests so parallel brands do not hit Noon at the same instant.
                    await Task.Delay(Random.Shared.Next(0, 1000), token);

                    var run = await RunBrandAsync(brand, token);
                    if (run is null)
                        return;

                    Interlocked.Add(ref products, run.Products);
                    if (run.Complete)
                        Interlocked.Increment(ref complete);
                    else
                        Interlocked.Increment(ref failed);
                });

            logger.LogInformation(
                "Noon cycle finished in {Seconds:0.0}s: {Brands} brands, {Complete} complete, {Failed} incomplete, {Products} products",
                stopwatch.Elapsed.TotalSeconds, brands.Count, complete, failed, products);

            cycles.Record(new NoonCycleSnapshot
            {
                CompletedAtUtc = DateTime.UtcNow,
                DurationSeconds = stopwatch.Elapsed.TotalSeconds,
                Brands = brands.Count,
                CompleteBrands = complete,
                IncompleteBrands = failed,
                Products = products,
                SuccessPages = _successPages,
                FailedPages = _failedPages,
                Page1Success = _page1Success
            });
        }

        private void LogRequest(NoonRequestLog entry)
        {
            _pendingLogs.Enqueue(entry);
            if (entry.Outcome != NoonRequestOutcome.Success)
            {
                Interlocked.Increment(ref _failedPages);
                return;
            }

            Interlocked.Increment(ref _successPages);
            if (entry.Page == 1)
                Interlocked.Increment(ref _page1Success);
        }

        public async Task<bool> RunBrandNowAsync(int brandId, CancellationToken ct = default)
        {
            var brand = await db.NoonBrandUrls.AsNoTracking().FirstOrDefaultAsync(b => b.Id == brandId, ct);
            return brand is not null && await RunBrandAsync(brand, ct) is not null;
        }

        private sealed record BrandRun(int Products, bool Complete);

        /// <summary>Null when the brand is already being scraped elsewhere.</summary>
        private async Task<BrandRun?> RunBrandAsync(NoonBrandUrl brand, CancellationToken ct)
        {
            if (!RunningBrands.TryAdd(brand.Id, 0))
                return null;

            try
            {
                var (hits, error) = await FetchAllPagesAsync(brand, ct);
                var products = await SaveBrandAsync(brand, hits, error, ct);
                return new BrandRun(products, error is null);
            }
            finally
            {
                RunningBrands.TryRemove(brand.Id, out _);
            }
        }

        /// <summary>All hits of the brand; error is null only when every page was fetched.</summary>
        private async Task<(List<NoonHit> Hits, string? Error)> FetchAllPagesAsync(NoonBrandUrl brand, CancellationToken ct)
        {
            try
            {
                var first = await catalog.FetchPageAsync(brand, 1, LogRequest, ct);
                if (first is null)
                    return ([], "Page 1 failed on every proxy attempt");

                // Page 1 tells the page count; the rest are fetched in parallel, each through its own proxy.
                var maxPages = Math.Max(1, _options.MaxPagesPerBrand);
                var lastPage = Math.Clamp(first.NbPages, 1, maxPages); // empty brands report nbPages 0
                var pages = new NoonCatalogResponse?[lastPage + 1];
                pages[1] = first;

                await Parallel.ForEachAsync(
                    Enumerable.Range(2, Math.Max(0, lastPage - 1)),
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelPages), CancellationToken = ct },
                    async (page, token) => pages[page] = await catalog.FetchPageAsync(brand, page, LogRequest, token));

                var hits = pages.Where(p => p is not null).SelectMany(p => p!.Hits).ToList();
                var failed = Enumerable.Range(1, lastPage).Where(p => pages[p] is null).ToList();

                if (failed.Count > 0)
                    return (hits, $"Pages {string.Join(", ", failed)} failed on every proxy attempt");

                return first.NbPages > maxPages
                    ? (hits, $"Brand has {first.NbPages} pages; only the first {maxPages} were scraped")
                    : (hits, null);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning("Noon: {Brand} skipped — {Error}", brand.Name, ex.Message);
                return ([], ex.Message);
            }
        }

        /// <summary>
        /// Upserts the hits, records price changes and sends drop alerts. Products missing from the
        /// brand get a NotAvailableDate only after a complete run, like Amazon.
        /// </summary>
        private async Task<int> SaveBrandAsync(NoonBrandUrl brand, List<NoonHit> hits, string? error, CancellationToken ct)
        {
            await SaveGate.WaitAsync(ct);
            try
            {
                var brandRow = await db.NoonBrandUrls.FindAsync([brand.Id], ct);
                if (brandRow is null)
                    return 0;

                var now = DateTime.UtcNow;
                var bySku = hits
                    .Where(h => !string.IsNullOrWhiteSpace(h.Sku))
                    .GroupBy(h => h.Sku!)
                    .ToDictionary(g => g.Key, g => g.OrderBy(h => h.CurrentPrice ?? decimal.MaxValue).First());

                var skus = bySku.Keys.ToList();
                var existing = await db.NoonProducts
                    .Where(p => skus.Contains(p.Sku))
                    .ToDictionaryAsync(p => p.Sku, ct);

                var alerts = new List<NoonDropAlert>();
                foreach (var (sku, hit) in bySku)
                {
                    var isNew = !existing.TryGetValue(sku, out var product);
                    if (isNew)
                    {
                        product = new NoonProduct { Sku = sku, CreatedAt = now, Currency = _options.Currency };
                        db.NoonProducts.Add(product);
                    }
                    else if (product!.IsBlocked)
                    {
                        continue;
                    }

                    var previousPrice = product!.CurrentPrice;
                    ApplyHit(product, hit, brand.Id, now);

                    if (isNew || previousPrice != product.CurrentPrice)
                        product.PriceHistory.Add(new NoonPriceHistory
                        {
                            Price = product.CurrentPrice,
                            OriginalPrice = product.OriginalPrice,
                            CheckedAt = now
                        });

                    TrackPrice(product, previousPrice, isNew, alerts);
                }

                if (error is null)
                {
                    var missing = await db.NoonProducts
                        .Where(p => p.NoonBrandUrlId == brand.Id && !p.IsBlocked && !skus.Contains(p.Sku))
                        .ToListAsync(ct);
                    foreach (var product in missing)
                    {
                        product.NotAvailableDate ??= now;
                        product.LastCheckedAt = now;
                    }
                }

                brandRow.LastRunAt = now;
                brandRow.LastRunError = error;
                brandRow.LastRunProductCount = bySku.Count;

                await SaveChangesAsync(ct);

                if (alerts.Count > 0)
                {
                    await SendAlertsAsync(alerts, ct);
                    await SaveChangesAsync(ct);
                }

                return bySku.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Noon: saving {Brand} failed", brand.Name);
                db.ChangeTracker.Clear();
                await db.NoonBrandUrls.Where(b => b.Id == brand.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.LastRunAt, DateTime.UtcNow)
                    .SetProperty(b => b.LastRunError, $"Save failed: {ex.Message}"), ct);
                return 0;
            }
            finally
            {
                db.ChangeTracker.Clear();
                SaveGate.Release();
            }
        }

        private async Task SaveChangesAsync(CancellationToken ct)
        {
            while (_pendingLogs.TryDequeue(out var log))
                db.NoonRequestLogs.Add(log);

            await db.SaveChangesAsync(ct);
        }

        private void ApplyHit(NoonProduct product, NoonHit hit, int brandId, DateTime now)
        {
            product.Name = hit.Name ?? product.Name;
            product.Brand = hit.Brand ?? hit.BrandCode;
            product.Url = catalog.BuildProductUrl(hit.PdpUrl);
            product.ImageUrl = hit.ImageUrl;
            product.StoreName = hit.StoreName;
            product.CurrentPrice = hit.CurrentPrice;
            product.OriginalPrice = hit.Price;
            product.Rating = hit.ProductRating?.Value;
            product.ReviewsCount = hit.ProductRating?.Count;
            product.IsBestSeller = hit.IsBestSeller == true;
            product.IsAvailable = true;
            product.NotAvailableDate = null;
            product.LastCheckedAt = now;
            product.NoonBrandUrlId = brandId;
        }

        /// <summary>Same tracking as Amazon products: baseline drop %, lowest/highest, and a drop alert vs the previous price.</summary>
        private static void TrackPrice(NoonProduct product, decimal? previousPrice, bool isNew, List<NoonDropAlert> alerts)
        {
            product.DropPercent = null;
            if (product.CurrentPrice is not { } price || price <= 0)
                return;

            product.LowestPrice = Math.Min(product.LowestPrice ?? price, price);
            product.HighestPrice = Math.Max(product.HighestPrice ?? price, price);
            product.DropBaselinePrice ??= previousPrice is > 0 ? previousPrice : price;

            if (TryPercentOff(product.DropBaselinePrice.Value, price, out var fromBaseline))
                product.DropPercent = RoundPct(fromBaseline);

            if (!isNew && AlertDropPercent(previousPrice, price) is { } drop)
                alerts.Add(new NoonDropAlert { Product = product, DropPercent = drop, BaselinePrice = previousPrice, CurrentPrice = price });
        }

        private async Task SendAlertsAsync(List<NoonDropAlert> alerts, CancellationToken ct)
        {
            var ids = alerts.Select(a => a.Product.Id).ToList();

            var history = (await db.NoonPriceHistories.AsNoTracking()
                    .Where(h => ids.Contains(h.NoonProductId) && h.Price != null)
                    .OrderBy(h => h.CheckedAt).ThenBy(h => h.Id)
                    .Select(h => new { h.NoonProductId, h.CheckedAt, Price = h.Price!.Value })
                    .ToListAsync(ct))
                .ToLookup(h => h.NoonProductId, h => (h.CheckedAt, h.Price));

            var amazon = await db.ProductLinks.AsNoTracking()
                .Where(l => ids.Contains(l.NoonProductId))
                .Select(l => new { l.NoonProductId, l.Product!.CurrentPrice, l.Product.Url, l.Product.IsAvailable })
                .ToDictionaryAsync(l => l.NoonProductId, ct);

            foreach (var alert in alerts)
            {
                ApplyHistory(alert, history[alert.Product.Id].ToList());
                if (amazon.TryGetValue(alert.Product.Id, out var linked))
                    alert.Linked = new LinkedOffer("أمازون", linked.CurrentPrice, linked.Url, linked.IsAvailable);

                if (!QualifiesForAlert(alert))
                    continue;

                if (await telegram.NotifyNoonDropAsync(alert, ct))
                    alert.Product.LastDropAlertPercent = alert.DropPercent;
            }
        }
    }
}
