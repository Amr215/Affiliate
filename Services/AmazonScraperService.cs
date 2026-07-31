using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Affiliate.Data;
using Affiliate.Models;
using Affiliate.Models.Dtos;
using Affiliate.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    public interface IAmazonScraperService
    {
        Task ProcessScheduledSearchesAsync(CancellationToken cancellationToken = default);

        /// <summary>Runs one search immediately. Returns false if not found or another run is in progress.</summary>
        Task<bool> RunSearchNowAsync(int searchId, CancellationToken cancellationToken = default);

        /// <summary>Re-checks available products by ASIN batches. Waits if a keyword scrape is in progress.</summary>
        Task ProcessAsinRecheckAsync(CancellationToken cancellationToken = default);
    }

    public class AmazonScraperService : IAmazonScraperService
    {
        private const string OxylabsApiUrl = "https://realtime.oxylabs.io/v1/queries";
        private const string OxylabsUsername = "AmrAmin_QTiRh";
        private const string OxylabsPassword = "7cJU=ilkHUq8VEa";
        private const decimal PriceDropAlertThresholdPercent = 10m;
        private const int MaxPagesPerSearch = 50;

        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        private readonly AffiliateDbContext _db;
        private readonly IScraperRunCoordinator _runCoordinator;
        private readonly ITelegramNotifier _telegram;
        private readonly AsinRecheckOptions _asinRecheck;
        private readonly ILogger<AmazonScraperService> _logger;

        public AmazonScraperService(
            AffiliateDbContext dbContext,
            IScraperRunCoordinator runCoordinator,
            ITelegramNotifier telegramNotifier,
            IOptions<AsinRecheckOptions> asinRecheckOptions,
            ILogger<AmazonScraperService> logger)
        {
            _db = dbContext;
            _runCoordinator = runCoordinator;
            _telegram = telegramNotifier;
            _asinRecheck = asinRecheckOptions.Value;
            _logger = logger;
        }

        public async Task ProcessScheduledSearchesAsync(CancellationToken ct = default)
        {
            _logger.LogDebug("Keyword scheduler waiting for scrape lock (if ASIN recheck is running)");
            await _runCoordinator.WaitEnterAsync(ct);

            try
            {
                var now = DateTime.UtcNow;
                var searches = await _db.ScraperSearches
                    .Where(s => s.IsEnabled)
                    .OrderBy(s => s.LastRunAt ?? DateTime.MinValue)
                    .ToListAsync(ct);

                foreach (var search in searches)
                {
                    ct.ThrowIfCancellationRequested();
                    if (search.IsDue(now))
                        await ExecuteSearchAsync(search, ct);
                }
            }
            finally
            {
                _runCoordinator.Release();
            }
        }

        public async Task<bool> RunSearchNowAsync(int searchId, CancellationToken ct = default)
        {
            if (!await _runCoordinator.TryEnterAsync(ct))
            {
                _logger.LogDebug("Run now skipped — a search is already running");
                return false;
            }

            try
            {
                var search = await _db.ScraperSearches.FirstOrDefaultAsync(s => s.Id == searchId, ct);
                if (search is null)
                    return false;

                await ExecuteSearchAsync(search, ct);
                return true;
            }
            finally
            {
                _runCoordinator.Release();
            }
        }

        public async Task ProcessAsinRecheckAsync(CancellationToken ct = default)
        {
            if (!_asinRecheck.Enabled)
            {
                _logger.LogDebug("ASIN recheck skipped — disabled in config");
                return;
            }

            _logger.LogInformation("ASIN recheck waiting for scrape lock (if keyword scrape is running)");
            await _runCoordinator.WaitEnterAsync(ct);

            try
            {
                var batchSize = Math.Max(1, _asinRecheck.BatchSize);
                var asinsPerPoll = Math.Max(batchSize, _asinRecheck.AsinsPerPoll);
                const string domain = "eg";

                var asins = await _db.Products
                    .Where(p => p.IsAvailable && p.IsBlocked != true && p.Asin != null && p.Asin != "")
                    .OrderBy(p => p.LastCheckedAt)
                    .ThenBy(p => p.Id)
                    .Take(asinsPerPoll)
                    .Select(p => p.Asin!)
                    .ToListAsync(ct);

                if (asins.Count == 0)
                {
                    _logger.LogInformation("ASIN recheck: no available products to check");
                    return;
                }

                _logger.LogInformation(
                    "ASIN recheck starting — {Count} ASINs in batches of {BatchSize}",
                    asins.Count, batchSize);

                var updated = 0;
                var unavailable = 0;

                for (var offset = 0; offset < asins.Count; offset += batchSize)
                {
                    ct.ThrowIfCancellationRequested();

                    var batch = asins.Skip(offset).Take(batchSize).ToList();
                    var batchIndex = offset / batchSize + 1;
                    var batchCount = (asins.Count + batchSize - 1) / batchSize;

                    try
                    {
                        var organic = await FetchAsinBatchAsync(batch, batchIndex, ct);
                        var returned = new HashSet<string>(
                            organic
                                .Where(p => !string.IsNullOrWhiteSpace(p.Asin))
                                .Select(p => p.Asin!),
                            StringComparer.OrdinalIgnoreCase);

                        if (organic.Count > 0)
                            updated += await SaveProductsAsync(organic, domain, ct);

                        var missing = batch.Where(a => !returned.Contains(a)).ToList();
                        if (missing.Count > 0)
                            unavailable += await RecordNotAvailableAsync(missing, ct);

                        _logger.LogInformation(
                            "ASIN recheck batch {Index}/{Total}: requested={Requested}, returned={Returned}, unavailable={Unavailable}",
                            batchIndex, batchCount, batch.Count, returned.Count, missing.Count);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Persist any queued request logs from the failed call, then continue other batches.
                        await _db.SaveChangesAsync(ct);
                        _logger.LogError(ex,
                            "ASIN recheck batch {Index}/{Total} failed ({Count} ASINs)",
                            batchIndex, batchCount, batch.Count);
                    }
                }

                _logger.LogInformation(
                    "ASIN recheck completed — updated={Updated}, recordedNotAvailable={Unavailable}",
                    updated, unavailable);
            }
            finally
            {
                _runCoordinator.Release();
            }
        }

        private async Task ExecuteSearchAsync(ScraperSearch search, CancellationToken ct)
        {
            _logger.LogInformation(
                "Running search {SearchId} ({Name}): query={Query}, domain={Domain}",
                search.Id, search.Name, search.Query, search.Domain);

            try
            {
                var products = await FetchAllPagesAsync(search, ct);

                if (products.Count == 0)
                {
                    await MarkSearchRunAsync(search, "No organic products parsed from response", ct);
                    _logger.LogWarning("Search {SearchId}: no organic products", search.Id);
                    return;
                }

                var saved = await SaveProductsAsync(products, search.Domain, ct, search.Id);
                await MarkSearchRunAsync(search, error: null, ct);
                _logger.LogInformation(
                    "Search {SearchId} completed — saved/updated {Saved} of {Total} products",
                    search.Id, saved, products.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // MarkSearchRunAsync also flushes any request logs queued before the failure.
                await MarkSearchRunAsync(search, ex.Message, ct);
                _logger.LogError(ex, "Search {SearchId} failed", search.Id);
            }
        }

        /// <summary>
        /// Fetches pages starting at <see cref="ScraperSearch.StartPage"/> up to the last visible
        /// page (capped at <see cref="MaxPagesPerSearch"/>). A failure on the first page aborts the
        /// run; a failure on a later page keeps the products already collected.
        /// </summary>
        private async Task<List<OrganicProduct>> FetchAllPagesAsync(ScraperSearch search, CancellationToken ct)
        {
            var all = new List<OrganicProduct>();
            var firstPage = Math.Max(1, search.StartPage);
            var lastPage = firstPage;

            for (var page = firstPage; page - firstPage < MaxPagesPerSearch; page++)
            {
                ct.ThrowIfCancellationRequested();

                SearchPageResult result;
                try
                {
                    result = await FetchSearchPageAsync(search, page, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (all.Count == 0)
                        throw;

                    _logger.LogWarning(ex,
                        "Search {SearchId}: stopped at page {Page}; keeping {Count} products from earlier pages",
                        search.Id, page, all.Count);
                    break;
                }

                all.AddRange(result.Organic);
                lastPage = result.LastVisiblePage ?? lastPage;

                _logger.LogInformation(
                    "Search {SearchId}: page {Page}/{Last} — {Count} organic products",
                    search.Id, page, lastPage, result.Organic.Count);

                if (page >= lastPage)
                    break;
            }

            return all;
        }

        /// <summary>Requests a single search page, logs the call, and throws on any failure.</summary>
        private async Task<SearchPageResult> FetchSearchPageAsync(ScraperSearch search, int page, CancellationToken ct)
        {
            var requestJson = JsonSerializer.Serialize(BuildRequestPayload(search, page));
            var requestedAt = DateTime.UtcNow;
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await Http.PostAsync(OxylabsApiUrl, content, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                QueueLog(search.Id, page, requestedAt, 0, "TransportError", requestJson, ex.Message);
                throw new InvalidOperationException($"Oxylabs request failed on page {page}: {ex.Message}", ex);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(ct);
                QueueLog(search.Id, page, requestedAt, status, response.ReasonPhrase, requestJson,
                    status == 200 ? null : body);

                if (status != 200)
                    throw new InvalidOperationException(
                        $"Oxylabs API failed on page {page} with status {status} ({response.StatusCode})");

                var api = JsonSerializer.Deserialize<OxylabsApiResponse>(body, JsonOptions);
                if (api?.Results is not { Count: > 0 })
                    throw new InvalidOperationException($"No results returned from Oxylabs API on page {page}");

                var data = api.Results[0].Content;
                return new SearchPageResult(data?.Results?.Organic ?? [], data?.LastVisiblePage);
            }
        }

        private async Task<List<OrganicProduct>> FetchAsinBatchAsync(
            IReadOnlyList<string> asins, int batchIndex, CancellationToken ct)
        {
            var query = string.Join("|", asins);
            var requestJson = JsonSerializer.Serialize(BuildAsinBatchPayload(query));
            var requestedAt = DateTime.UtcNow;
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await Http.PostAsync(OxylabsApiUrl, content, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                QueueLog(null, batchIndex, requestedAt, 0, "TransportError", requestJson, ex.Message);
                throw new InvalidOperationException(
                    $"Oxylabs ASIN batch {batchIndex} failed: {ex.Message}", ex);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(ct);
                QueueLog(null, batchIndex, requestedAt, status, response.ReasonPhrase, requestJson,
                    status == 200 ? null : body);

                if (status != 200)
                    throw new InvalidOperationException(
                        $"Oxylabs ASIN batch {batchIndex} failed with status {status} ({response.StatusCode})");

                var api = JsonSerializer.Deserialize<OxylabsApiResponse>(body, JsonOptions);
                if (api?.Results is not { Count: > 0 })
                    throw new InvalidOperationException(
                        $"No results returned from Oxylabs API for ASIN batch {batchIndex}");

                return api.Results[0].Content?.Results?.Organic ?? [];
            }
        }

        private static object BuildAsinBatchPayload(string query) => new
        {
            source = "amazon_search",
            domain = "eg",
            query,
            locale = "en-AE",
            start_page = 1,
            pages = 1,
            parse = true,
            // context = new object[]
            // {
            //     new { key = "force_headers", value = false },
            //     new { key = "force_cookies", value = false },
            //     new { key = "hc_policy", value = true },
            //     new { key = "merchant_id", value = "A1ZVRGNO5AYLOV" },
            //     new { key = "safe_search", value = true },
            //     new { key = "currency", value = "EGP" },
            //     new { key = "sort_by", value = "featured" },
            //     new { key = "geo_location", value = "Cairo" }
            // }
        };

        private void QueueLog(int? searchId, int page, DateTime requestedAt, int statusCode,
            string? statusPhrase, string requestBody, string? responseBody)
        {
            _db.OxylabsRequestLogs.Add(new OxylabsRequestLog
            {
                ScraperSearchId = searchId,
                Page = page,
                RequestedAt = requestedAt,
                StatusCode = statusCode,
                StatusPhrase = Truncate(statusPhrase, 64),
                RequestBody = requestBody,
                ResponseBody = responseBody
            });
        }

        private async Task MarkSearchRunAsync(ScraperSearch search, string? error, CancellationToken ct)
        {
            search.LastRunAt = DateTime.UtcNow;
            search.LastRunError = Truncate(error, 2000);
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Records the first miss date for ASINs that did not return.
        /// Does not flip <see cref="Product.IsAvailable"/> — that stays manual from the products list.
        /// </summary>
        private async Task<int> RecordNotAvailableAsync(IReadOnlyList<string> asins, CancellationToken ct)
        {
            if (asins.Count == 0)
                return 0;

            var products = await _db.Products
                .Where(p => p.Asin != null && asins.Contains(p.Asin) && p.IsBlocked != true)
                .ToListAsync(ct);

            var checkedAt = DateTime.UtcNow;
            foreach (var product in products)
            {
                product.LastCheckedAt = checkedAt;
                product.NotAvailableDate ??= checkedAt;
            }

            await _db.SaveChangesAsync(ct);
            return products.Count;
        }

        private async Task<int> SaveProductsAsync(
            List<OrganicProduct> products,
            string domain,
            CancellationToken ct,
            int? scraperSearchId = null)
        {
            // Keep the last occurrence per ASIN, dropping entries without one.
            var byAsin = new Dictionary<string, OrganicProduct>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in products)
            {
                if (string.IsNullOrWhiteSpace(p.Asin))
                    _logger.LogWarning("Skipping product without ASIN: {Title}", p.Title);
                else
                    byAsin[p.Asin] = p;
            }

            if (byAsin.Count == 0)
                return 0;

            var existing = await _db.Products
                .Where(p => p.Asin != null && byAsin.Keys.Contains(p.Asin))
                .ToDictionaryAsync(p => p.Asin!, StringComparer.OrdinalIgnoreCase, ct);

            var checkedAt = DateTime.UtcNow;
            var alerts = new List<ProductDropAlert>();

            var saved = 0;
            foreach (var (asin, org) in byAsin)
            {
                var isNew = !existing.TryGetValue(asin, out var product);

                // Blocked products must not be updated or get price history from Oxylabs.
                if (!isNew && product!.IsBlocked == true)
                    continue;

                var previousPrice = product?.CurrentPrice;
                // Only write Price History on a real price change (or first sighting). Increases and
                // drops both get a row; an identical price does not.
                var priceUnchanged = !isNew && previousPrice == org.Price;

                if (isNew)
                {
                    product = new Product { Asin = asin, CreatedAt = checkedAt };
                    _db.Products.Add(product);
                    existing[asin] = product;
                }

                if (scraperSearchId.HasValue)
                    product!.ScraperSearchId = scraperSearchId;

                ApplyOrganicData(product!, org, domain, checkedAt, isNew);

                if (!priceUnchanged)
                    product!.PriceHistory.Add(CreatePriceHistory(org, checkedAt));

                ApplyPriceTracking(product!, org.Price, previousPrice, isNew, alerts);
                saved++;
            }

            if (saved == 0 && alerts.Count == 0)
                return 0;

            await _db.SaveChangesAsync(ct);

            if (alerts.Count > 0)
            {
                await AttachPriceHistoryAsync(alerts, ct);
                if (await DispatchAlertsAsync(alerts, ct))
                    await _db.SaveChangesAsync(ct);
            }

            return saved;
        }

        /// <summary>
        /// Attaches the full price history (oldest → newest) for each alert, with the date recorded.
        /// </summary>
        private async Task AttachPriceHistoryAsync(List<ProductDropAlert> alerts, CancellationToken ct)
        {
            foreach (var alert in alerts)
            {
                var points = await _db.PriceHistories.AsNoTracking()
                    .Where(h => h.ProductId == alert.Product.Id && h.Price != null)
                    .OrderBy(h => h.CheckedAt)
                    .ThenBy(h => h.Id)
                    .Select(h => new { h.CheckedAt, h.Price })
                    .ToListAsync(ct);

                if (points.Count == 0)
                    continue;

                alert.History = points
                    .Select(p => new PriceHistoryPoint(string.Empty, p.Price, p.CheckedAt))
                    .ToList();
            }
        }

        /// <summary>
        /// Product.DropPercent is vs the first observed price (DropBaselinePrice) for UI.
        /// Telegram alerts fire only when the new price is ≥10% below the last recorded price.
        /// Increases and smaller drops are recorded in Price History but do not notify.
        /// Products seen for the first time never alert.
        /// </summary>
        private static void ApplyPriceTracking(
            Product product, decimal? newPrice, decimal? previousPrice, bool isNew, List<ProductDropAlert> alerts)
        {
            product.DropPercent = null;

            if (newPrice is not > 0)
                return;

            // First known price is permanent baseline for the life of the product (UI DropPercent).
            if (product.DropBaselinePrice is not > 0)
                product.DropBaselinePrice = previousPrice is > 0 ? previousPrice : newPrice;

            if (product.DropBaselinePrice is > 0 &&
                TryPercentOff(product.DropBaselinePrice.Value, newPrice.Value, out var dropPct))
                product.DropPercent = RoundPct(dropPct);

            if (isNew)
                return;

            // Alert only on a drop of ≥ threshold vs the last recorded price (not baseline).
            if (previousPrice is not > 0)
                return;

            if (!TryPercentOff(previousPrice.Value, newPrice.Value, out var dropFromLast))
                return; // same price, increase, or invalid — history already handled; no notify

            var drop = RoundPct(dropFromLast);
            if (drop < PriceDropAlertThresholdPercent)
                return;

            alerts.Add(new ProductDropAlert
            {
                Product = product,
                DropPercent = drop,
                BaselinePrice = previousPrice,
                CurrentPrice = newPrice
            });
        }

        private async Task<bool> DispatchAlertsAsync(List<ProductDropAlert> alerts, CancellationToken ct)
        {
            var sentAny = false;
            foreach (var alert in alerts)
            {
                if (!await _telegram.NotifyDropAsync(alert, ct))
                    continue;

                sentAny = true;
                alert.Product.LastDropAlertPercent = alert.DropPercent;
            }

            return sentAny;
        }

        private static bool TryPercentOff(decimal original, decimal current, out decimal percent)
        {
            percent = 0;
            if (original <= 0 || current < 0 || current >= original)
                return false;
            percent = (original - current) / original * 100m;
            return true;
        }

        private static decimal RoundPct(decimal percent) =>
            Math.Round(percent, 2, MidpointRounding.AwayFromZero);

        private static object BuildRequestPayload(ScraperSearch search, int startPage)
        {
            var context = new List<object>();
            AddContext(context, "force_headers", search.ForceHeaders);
            AddContext(context, "force_cookies", search.ForceCookies);
            AddContext(context, "hc_policy", search.HcPolicy);
            AddContext(context, "category_id", search.CategoryId);
            AddContext(context, "merchant_id", search.MerchantId);
            AddContext(context, "check_empty_geo", search.CheckEmptyGeo);
            AddContext(context, "safe_search", search.SafeSearch);
            AddContext(context, "currency", search.Currency);
            AddContext(context, "sort_by", search.SortBy);
            AddContext(context, "refinements", BuildRefinements(search));
            AddContext(context, "geo_location", search.GeoLocation);

            return new
            {
                source = search.Source,
                domain = search.Domain,
                query = search.Query,
                locale = search.Locale,
                start_page = startPage,
                pages = 1,
                parse = search.Parse,
                context
            };
        }

        /// <summary>Amazon p_36 price filter in cents (DB units × 100).</summary>
        private static List<string>? BuildRefinements(ScraperSearch search)
        {
            var items = new List<string>();
            if (!string.IsNullOrWhiteSpace(search.Refinements))
                items.Add(search.Refinements.Trim());

            if (search.MinPrice.HasValue || search.MaxPrice.HasValue)
            {
                var minCents = (search.MinPrice ?? 0) * 100;
                var maxPart = search.MaxPrice.HasValue ? (search.MaxPrice.Value * 100).ToString() : "";
                items.Add($"p_36:{minCents}-{maxPart}");
            }

            return items.Count > 0 ? items : null;
        }

        private static void AddContext(List<object> context, string key, object? value)
        {
            if (value is null)
                return;
            if (value is string s && string.IsNullOrWhiteSpace(s))
                return;
            if (value is System.Collections.ICollection { Count: 0 })
                return;
            context.Add(new { key, value });
        }

        private static void ApplyOrganicData(
            Product product, OrganicProduct org, string domain, DateTime checkedAt, bool isNew)
        {
            product.Name = org.Title ?? string.Empty;
            product.Url = BuildProductUrl(org.Url, domain);
            product.CurrentPrice = org.Price;
            product.Rating = org.Rating;
            product.ReviewsCount = org.ReviewsCount;
            product.IsPrime = org.IsPrime;
            product.IsSponsored = org.IsSponsored;
            product.IsBestSeller = org.BestSeller;
            product.Currency = org.Currency;
            product.Manufacturer = org.Manufacturer;
            product.ImageUrl = org.ImageUrl;
            product.Position = org.Position;
            product.ShippingInformation = org.ShippingInformation;
            product.IsAvailable = true;
            product.Status = "Available";
            product.NotAvailableDate = null;
            product.LastCheckedAt = checkedAt;

            var low = org.Price ?? org.PriceUpper;
            var high = org.PriceUpper ?? org.Price;

            if (isNew)
            {
                product.LowestPrice = low;
                product.HighestPrice = high;
                return;
            }

            if (low.HasValue)
                product.LowestPrice = product.LowestPrice is { } lo ? Math.Min(lo, low.Value) : low;
            if (high.HasValue)
                product.HighestPrice = product.HighestPrice is { } hi ? Math.Max(hi, high.Value) : high;
        }

        private static PriceHistory CreatePriceHistory(OrganicProduct org, DateTime checkedAt) => new()
        {
            Price = org.Price,
            PriceUpper = org.PriceUpper,
            CheckedAt = checkedAt,
            Status = "Available",
            Rating = org.Rating,
            ReviewsCount = org.ReviewsCount,
            IsPrime = org.IsPrime,
            IsSponsored = org.IsSponsored,
            IsBestSeller = org.BestSeller,
            ShippingInformation = org.ShippingInformation
        };

        private static string BuildProductUrl(string? relativeUrl, string domain)
        {
            var baseUrl = $"https://www.amazon.{domain.Trim()}";
            if (string.IsNullOrWhiteSpace(relativeUrl))
                return baseUrl;
            return relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? relativeUrl
                : $"{baseUrl}{relativeUrl}";
        }

        private static string? Truncate(string? value, int maxLength) =>
            value is null || value.Length <= maxLength ? value : value[..maxLength];

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($"{OxylabsUsername}:{OxylabsPassword}")));
            return client;
        }

        private sealed record SearchPageResult(List<OrganicProduct> Organic, int? LastVisiblePage);
    }
}
