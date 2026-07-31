using System.Collections.Concurrent;
using System.Diagnostics;
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

        /// <summary>Runs one URL scrape immediately. Returns false if not found or another run is in progress.</summary>
        Task<bool> RunSearchNowAsync(int scraperUrlId, CancellationToken cancellationToken = default);

        /// <summary>Re-checks available products in batches of ASINs via one Amazon search URL per batch (ISP).</summary>
        Task ProcessAsinRecheckAsync(CancellationToken cancellationToken = default);
    }

    public class AmazonScraperService : IAmazonScraperService
    {
        private const decimal PriceDropAlertThresholdPercent = 10m;
        private const int MaxPagesPerSearch = 50;

        private readonly AffiliateDbContext _db;
        private readonly IScraperRunCoordinator _runCoordinator;
        private readonly ITelegramNotifier _telegram;
        private readonly AsinRecheckOptions _asinRecheck;
        private readonly IIspProxyRoundRobin _ispProxyRoundRobin;
        private readonly ILogger<AmazonScraperService> _logger;
        private readonly ConcurrentQueue<OxylabsRequestLog> _pendingLogs = new();

        public AmazonScraperService(
            AffiliateDbContext dbContext,
            IScraperRunCoordinator runCoordinator,
            ITelegramNotifier telegramNotifier,
            IOptions<AsinRecheckOptions> asinRecheckOptions,
            IIspProxyRoundRobin ispProxyRoundRobin,
            ILogger<AmazonScraperService> logger)
        {
            _db = dbContext;
            _runCoordinator = runCoordinator;
            _telegram = telegramNotifier;
            _asinRecheck = asinRecheckOptions.Value;
            _ispProxyRoundRobin = ispProxyRoundRobin;
            _logger = logger;
        }

        public async Task ProcessScheduledSearchesAsync(CancellationToken ct = default)
        {
            _logger.LogDebug("URL scraper scheduler waiting for scrape lock (if ASIN recheck is running)");
            await _runCoordinator.WaitEnterAsync(ct);

            try
            {
                var now = DateTime.UtcNow;
                var urls = await _db.ScraperUrls
                    .Where(s => s.IsEnabled)
                    .OrderBy(s => s.LastRunAt ?? DateTime.MinValue)
                    .ToListAsync(ct);

                foreach (var item in urls)
                {
                    ct.ThrowIfCancellationRequested();
                    if (item.IsDue(now))
                        await ExecuteUrlScrapeAsync(item, ct);
                }
            }
            finally
            {
                _runCoordinator.Release();
            }
        }

        public async Task<bool> RunSearchNowAsync(int scraperUrlId, CancellationToken ct = default)
        {
            if (!await _runCoordinator.TryEnterAsync(ct))
            {
                _logger.LogDebug("Run now skipped — a scrape is already running");
                return false;
            }

            try
            {
                var item = await _db.ScraperUrls.FirstOrDefaultAsync(s => s.Id == scraperUrlId, ct);
                if (item is null)
                    return false;

                await ExecuteUrlScrapeAsync(item, ct);
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

            _logger.LogInformation("ASIN recheck waiting for scrape lock (if URL scrape is running)");
            await _runCoordinator.WaitEnterAsync(ct);

            try
            {
                var batchSize = Math.Clamp(_asinRecheck.BatchSize, 2, 48);
                var asinsPerPoll = Math.Max(batchSize, _asinRecheck.AsinsPerPoll);
                var domain = string.IsNullOrWhiteSpace(_asinRecheck.Domain) ? "eg" : _asinRecheck.Domain.Trim();

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

                var batches = SplitIntoBatches(asins, batchSize);
                if (batches.Count == 0)
                {
                    _logger.LogInformation("ASIN recheck: not enough ASINs for a batch (need ≥2)");
                    return;
                }

                var maxParallel = ResolveParallelism(batches.Count);
                var startedAt = Stopwatch.GetTimestamp();

                _logger.LogInformation(
                    "ASIN recheck starting via ISP — {Count} ASINs in {Batches} search request(s) of up to {BatchSize}, {Parallel} in parallel",
                    asins.Count, batches.Count, batchSize, maxParallel);

                // Fetch phase: parallel across proxy ports. Nothing here may touch the DbContext.
                var results = new BatchResult?[batches.Count];

                await Parallel.ForEachAsync(
                    Enumerable.Range(0, batches.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct },
                    async (index, token) =>
                    {
                        var batch = batches[index];

                        if (maxParallel > 1)
                            await Task.Delay(AmazonBrowserProfile.BatchStaggerMs(index % maxParallel), token);
                        else if (index > 0)
                            await Task.Delay(AmazonBrowserProfile.BetweenAsinDelayMs(), token);

                        try
                        {
                            var organic = await FetchAsinBatchSearchViaIspAsync(batch, domain, index + 1, token);
                            results[index] = new BatchResult(batch, organic, null);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            results[index] = new BatchResult(batch, [], ex);
                        }
                    });

                // Save phase: sequential (the DbContext is single-threaded) and batched into one
                // round trip each, so 10 fetches don't become 30 database calls.
                var failedBatches = 0;
                var succeeded = new List<BatchResult>(results.Length);
                var allOrganic = new List<OrganicProduct>();

                foreach (var result in results)
                {
                    if (result is null)
                        continue;

                    if (result.Error is not null)
                    {
                        failedBatches++;
                        _logger.LogError(result.Error,
                            "ASIN recheck batch failed ({Count} ASINs)", result.Batch.Count);
                        continue;
                    }

                    succeeded.Add(result);
                    allOrganic.AddRange(result.Organic);
                }

                var returned = new HashSet<string>(
                    allOrganic
                        .Where(p => !string.IsNullOrWhiteSpace(p.Asin))
                        .Select(p => p.Asin!),
                    StringComparer.OrdinalIgnoreCase);

                // A batch's ASIN counts as missing only if no batch returned it.
                var missing = succeeded
                    .SelectMany(r => r.Batch)
                    .Where(a => !returned.Contains(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var updated = allOrganic.Count > 0
                    ? await SaveProductsAsync(allOrganic, domain, ct)
                    : 0;

                var unavailable = missing.Count > 0
                    ? await RecordNotAvailableAsync(missing, ct)
                    : 0;

                await SaveAsync(ct);

                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                _logger.LogInformation(
                    "ASIN recheck completed in {Seconds:0.0}s — {Asins} ASINs, updated={Updated}, recordedNotAvailable={Unavailable}, failedBatches={Failed}",
                    elapsed.TotalSeconds, asins.Count, updated, unavailable, failedBatches);
            }
            finally
            {
                _runCoordinator.Release();
            }
        }

        private sealed record BatchResult(List<string> Batch, List<OrganicProduct> Organic, Exception? Error);

        /// <summary>
        /// Splits ASINs into request-sized batches. A trailing single ASIN is topped up from the
        /// previous batch, because Amazon search needs at least two terms to behave like a keyword query.
        /// </summary>
        private static List<List<string>> SplitIntoBatches(List<string> asins, int batchSize)
        {
            var batches = new List<List<string>>();
            for (var offset = 0; offset < asins.Count; offset += batchSize)
                batches.Add(asins.Skip(offset).Take(batchSize).ToList());

            if (batches.Count == 1 && batches[0].Count < 2)
                return [];

            if (batches.Count > 1 && batches[^1].Count == 1)
            {
                var donor = batches[^2];
                batches[^1].Insert(0, donor[^1]);
                donor.RemoveAt(donor.Count - 1);
            }

            return batches;
        }

        /// <summary>Caps parallelism at the number of proxy IPs actually available right now.</summary>
        private int ResolveParallelism(int batchCount)
        {
            var configured = Math.Max(1, _asinRecheck.MaxParallelBatches);
            var healthy = Math.Max(1, _ispProxyRoundRobin.HealthyPortCount);

            if (configured > healthy)
            {
                _logger.LogWarning(
                    "ASIN recheck: only {Healthy} of {Configured} proxy IPs available; throughput will be lower this poll",
                    healthy, configured);
            }

            return Math.Clamp(Math.Min(configured, healthy), 1, batchCount);
        }

        private async Task ExecuteUrlScrapeAsync(ScraperUrl scraperUrl, CancellationToken ct)
        {
            _logger.LogInformation(
                "Running URL scrape {Id} ({Name}): url={Url}, domain={Domain}",
                scraperUrl.Id, scraperUrl.Name, scraperUrl.Url, scraperUrl.Domain);

            try
            {
                var products = await FetchAllPagesAsync(scraperUrl, ct);

                if (products.Count == 0)
                {
                    await MarkRunAsync(scraperUrl, "No organic products parsed from HTML", ct);
                    _logger.LogWarning("URL scrape {Id}: no organic products", scraperUrl.Id);
                    return;
                }

                var saved = await SaveProductsAsync(products, scraperUrl.Domain, ct, scraperUrl.Id);
                await MarkRunAsync(scraperUrl, error: null, ct);
                _logger.LogInformation(
                    "URL scrape {Id} completed — saved/updated {Saved} of {Total} products",
                    scraperUrl.Id, saved, products.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await MarkRunAsync(scraperUrl, ex.Message, ct);
                _logger.LogError(ex, "URL scrape {Id} failed", scraperUrl.Id);
            }
        }

        /// <summary>
        /// Fetches pages starting at <see cref="ScraperUrl.StartPage"/> up to the last visible
        /// page. On blank/captcha/bad HTML, quarantines that IP and retries the same page on another IP.
        /// </summary>
        private async Task<List<OrganicProduct>> FetchAllPagesAsync(ScraperUrl scraperUrl, CancellationToken ct)
        {
            var all = new List<OrganicProduct>();
            var firstPage = Math.Max(1, scraperUrl.StartPage);
            var lastPage = firstPage;

            var endpoint = _ispProxyRoundRobin.Next();
            var client = _ispProxyRoundRobin.CreateClient(endpoint);
            string? referer = null;
            var warmedHost = false;
            int? proxyPort = endpoint.UseProxy ? endpoint.Port : null;

            try
            {
                _logger.LogInformation(
                    "URL scrape {Id} starting on {Proxy}",
                    scraperUrl.Id, endpoint.Describe());

                await Task.Delay(AmazonBrowserProfile.BeforeFirstRequestDelayMs(), ct);

                for (var page = firstPage; page - firstPage < MaxPagesPerSearch; page++)
                {
                    ct.ThrowIfCancellationRequested();

                    if (page > firstPage)
                        await Task.Delay(AmazonBrowserProfile.NextPageDelayMs(), ct);

                    var pageOk = false;
                    var triedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { endpoint.Key };
                    var maxAttempts = Math.Max(1, _ispProxyRoundRobin.AvailablePortCount);

                    for (var attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        try
                        {
                            if (!warmedHost)
                            {
                                await WarmupAmazonHomeAsync(client, scraperUrl, endpoint, ct);
                                warmedHost = true;
                            }

                            var (result, pageUrl) = await FetchSearchPageAsync(
                                client, scraperUrl, page, referer, endpoint, proxyPort, ct);

                            all.AddRange(result.Organic);
                            lastPage = result.LastVisiblePage ?? lastPage;
                            referer = pageUrl;
                            pageOk = true;
                            _ispProxyRoundRobin.MarkHealthy(endpoint);

                            _logger.LogInformation(
                                "URL scrape {Id}: page {Page}/{Last} via {Proxy} — {Count} organic",
                                scraperUrl.Id, page, lastPage, endpoint.Describe(), result.Organic.Count);
                            break;
                        }
                        catch (AmazonFetchRejectedException ex)
                        {
                            if (ex.IsTransport)
                                _ispProxyRoundRobin.MarkTransient(endpoint, ex.Message);
                            else
                                _ispProxyRoundRobin.MarkBad(endpoint, ex.Message);

                            QueueLog(scraperUrl.Id, page, DateTime.UtcNow, ex.StatusCode, "Rejected",
                                $"{endpoint.Describe()} page={page}", Truncate(ex.Message, 2000), proxyPort);

                            if (attempt >= maxAttempts)
                            {
                                if (all.Count == 0)
                                    throw;

                                _logger.LogWarning(
                                    "URL scrape {Id}: giving up page {Page} after {Attempts} IPs; keeping {Count} products",
                                    scraperUrl.Id, page, attempt, all.Count);
                                return all;
                            }

                            client.Dispose();
                            endpoint = _ispProxyRoundRobin.Next(triedKeys);
                            triedKeys.Add(endpoint.Key);
                            client = _ispProxyRoundRobin.CreateClient(endpoint);
                            referer = null;
                            warmedHost = false;
                            proxyPort = endpoint.UseProxy ? endpoint.Port : null;

                            _logger.LogWarning(
                                "URL scrape {Id}: page {Page} failed on bad port ({Reason}); switching to {Proxy}",
                                scraperUrl.Id, page, ex.Message, endpoint.Describe());

                            await Task.Delay(AmazonBrowserProfile.AfterIpSwitchDelayMs(), ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException and not AmazonFetchRejectedException)
                        {
                            _ispProxyRoundRobin.MarkTransient(endpoint, DescribeTransport(ex));
                            QueueLog(scraperUrl.Id, page, DateTime.UtcNow, 0, "TransportError",
                                $"{endpoint.Describe()} page={page}", Truncate(DescribeTransport(ex), 2000), proxyPort);

                            if (attempt >= maxAttempts)
                            {
                                if (all.Count == 0)
                                    throw;

                                _logger.LogWarning(ex,
                                    "URL scrape {Id}: stopped at page {Page}; keeping {Count} products",
                                    scraperUrl.Id, page, all.Count);
                                return all;
                            }

                            client.Dispose();
                            endpoint = _ispProxyRoundRobin.Next(triedKeys);
                            triedKeys.Add(endpoint.Key);
                            client = _ispProxyRoundRobin.CreateClient(endpoint);
                            referer = null;
                            warmedHost = false;
                            proxyPort = endpoint.UseProxy ? endpoint.Port : null;

                            _logger.LogWarning(ex,
                                "URL scrape {Id}: transport error on page {Page}; switching to {Proxy}",
                                scraperUrl.Id, page, endpoint.Describe());

                            await Task.Delay(AmazonBrowserProfile.AfterIpSwitchDelayMs(), ct);
                        }
                    }

                    if (!pageOk)
                        break;

                    if (page >= lastPage)
                        break;
                }

                return all;
            }
            finally
            {
                client.Dispose();
            }
        }

        private Task WarmupAmazonHomeAsync(
            HttpClient client, ScraperUrl scraperUrl, IspProxyEndpoint endpoint, CancellationToken ct)
        {
            if (!Uri.TryCreate(scraperUrl.Url, UriKind.Absolute, out var uri))
                return Task.CompletedTask;

            return WarmupAmazonHomeAsync(
                client, $"{uri.Scheme}://{uri.Host}/", endpoint, $"URL scrape {scraperUrl.Id}", ct);
        }

        /// <summary>Fetches the Amazon homepage so the session carries real cookies into the next request.</summary>
        private async Task WarmupAmazonHomeAsync(
            HttpClient client, string home, IspProxyEndpoint endpoint, string label, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, home);
                AmazonBrowserProfile.ApplyNavigationHeaders(request, home, referer: null);
                request.Headers.Remove("Referer");
                request.Headers.Remove("Sec-Fetch-Site");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                _ = await response.Content.ReadAsStringAsync(ct);
                _logger.LogDebug(
                    "{Label}: warmed up {Home} via {Proxy} ({Status})",
                    label, home, endpoint.Describe(), (int)response.StatusCode);

                await Task.Delay(Random.Shared.Next(500, 1500), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "{Label}: homepage warmup failed (continuing)", label);
            }
        }

        private async Task<(SearchPageParseResult Result, string PageUrl)> FetchSearchPageAsync(
            HttpClient client,
            ScraperUrl scraperUrl,
            int page,
            string? referer,
            IspProxyEndpoint endpoint,
            int? proxyPort,
            CancellationToken ct)
        {
            var pageUrl = BuildPageUrl(scraperUrl.Url, page);
            var requestedAt = DateTime.UtcNow;
            var requestLog = $"{endpoint.Describe()} GET {pageUrl}";

            HttpResponseMessage response;
            try
            {
                response = await SendNavigationAsync(
                    client, pageUrl, referer, $"URL scrape {scraperUrl.Id} page {page}", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var reason = DescribeTransport(ex);
                QueueLog(scraperUrl.Id, page, requestedAt, 0, "TransportError", requestLog, reason, proxyPort);
                throw new AmazonFetchRejectedException(
                    $"Transport error on page {page}: {reason}", statusCode: 0, ex, isTransport: true);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(ct);
                QueueLog(scraperUrl.Id, page, requestedAt, status, response.ReasonPhrase, requestLog,
                    status == 200 ? null : Truncate(body, 8000), proxyPort);

                if (status is 403 or 429 or 503 or 502 or 500)
                    throw new AmazonFetchRejectedException(
                        $"Blocked/unavailable status {status} on page {page}", status);

                if (status != 200)
                    throw new AmazonFetchRejectedException(
                        $"HTTP {status} on page {page}", status);

                if (string.IsNullOrWhiteSpace(body))
                    throw new AmazonFetchRejectedException($"Empty HTML on page {page}", status);

                if (LooksLikeBotChallenge(body))
                    throw new AmazonFetchRejectedException(
                        $"Captcha/bot challenge on page {page}", status);

                if (body.Length < 8_000)
                    throw new AmazonFetchRejectedException(
                        $"Suspiciously short HTML ({body.Length} chars) on page {page}", status);

                if (!LooksLikeAmazonSearchHtml(body))
                    throw new AmazonFetchRejectedException(
                        $"Unexpected HTML (not a search results page) on page {page}", status);

                var parsed = AmazonSearchHtmlParser.Parse(body);

                if (parsed.Organic.Count == 0 && !LooksLikeZeroResultsPage(body))
                    throw new AmazonFetchRejectedException(
                        $"No products parsed and page does not look like zero-results on page {page}", status);

                return (parsed, pageUrl);
            }
        }

        private static bool LooksLikeBotChallenge(string html)
        {
            if (string.IsNullOrEmpty(html))
                return true;

            return html.Contains("api-services-support@amazon.com", StringComparison.OrdinalIgnoreCase)
                || html.Contains("Type the characters you see in this image", StringComparison.OrdinalIgnoreCase)
                || html.Contains("Enter the characters you see below", StringComparison.OrdinalIgnoreCase)
                || html.Contains("/errors/validateCaptcha", StringComparison.OrdinalIgnoreCase)
                || html.Contains("Robot Check", StringComparison.OrdinalIgnoreCase)
                || html.Contains("Sorry, we just need to make sure you're not a robot", StringComparison.OrdinalIgnoreCase)
                || html.Contains("opfcaptcha", StringComparison.OrdinalIgnoreCase)
                || html.Contains("captchacharacters", StringComparison.OrdinalIgnoreCase)
                || LooksLikeSoftBlockPage(html);
        }

        /// <summary>Amazon's throttling "Sorry! / عذرًا!" page, served with either 200 or 503.</summary>
        private static bool LooksLikeSoftBlockPage(string html) =>
            html.Contains("ref=cs_503", StringComparison.OrdinalIgnoreCase)
            || html.Contains("cs_503_logo", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Sorry! Something went wrong", StringComparison.OrdinalIgnoreCase)
            || html.Contains("communities/people/logo.gif", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeAmazonSearchHtml(string html) =>
            html.Contains("s-search-result", StringComparison.OrdinalIgnoreCase)
            || html.Contains("data-component-type=\"s-search-result\"", StringComparison.OrdinalIgnoreCase)
            || html.Contains("cel_widget_id=\"MAIN-SEARCH_RESULTS", StringComparison.OrdinalIgnoreCase)
            || html.Contains("s-main-slot", StringComparison.OrdinalIgnoreCase)
            || html.Contains("id=\"search\"", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeZeroResultsPage(string html) =>
            html.Contains("No results for", StringComparison.OrdinalIgnoreCase)
            || html.Contains("did not match any products", StringComparison.OrdinalIgnoreCase)
            || html.Contains("0 results for", StringComparison.OrdinalIgnoreCase)
            || html.Contains("لا توجد نتائج", StringComparison.OrdinalIgnoreCase);

        private sealed class AmazonFetchRejectedException : Exception
        {
            public int StatusCode { get; }

            /// <summary>True when no HTTP response arrived (dropped connection/timeout), not an Amazon block.</summary>
            public bool IsTransport { get; }

            public AmazonFetchRejectedException(
                string message, int statusCode, Exception? inner = null, bool isTransport = false)
                : base(message, inner)
            {
                StatusCode = statusCode;
                IsTransport = isTransport;
            }
        }

        /// <summary>Sends a navigation request, retrying dropped connections/timeouts on the same IP.</summary>
        private async Task<HttpResponseMessage> SendNavigationAsync(
            HttpClient client, string url, string? referer, string label, CancellationToken ct)
        {
            var retries = _ispProxyRoundRobin.TransportRetriesPerIp;

            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                if (attempt > 0)
                    await Task.Delay(AmazonBrowserProfile.TransportRetryDelayMs(attempt), ct);

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    AmazonBrowserProfile.ApplyNavigationHeaders(request, url, referer);
                    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                catch (Exception ex) when (attempt < retries && IsRetryableTransport(ex, ct))
                {
                    _logger.LogDebug(ex,
                        "{Label}: connection attempt {Attempt}/{Total} failed ({Reason}); retrying same IP",
                        label, attempt + 1, retries + 1, DescribeTransport(ex));
                }
            }
        }

        private static bool IsRetryableTransport(Exception ex, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return false;

            // A TaskCanceledException without ct cancellation is the HttpClient timeout.
            return ex is HttpRequestException
                or IOException
                or System.Net.Sockets.SocketException
                or TaskCanceledException;
        }

        /// <summary>Unwraps the inner reason — "An error occurred while sending the request." alone says nothing.</summary>
        private static string DescribeTransport(Exception ex)
        {
            if (ex is TaskCanceledException && ex.InnerException is TimeoutException)
                return "request timed out";

            var parts = new List<string>();
            for (var current = ex; current is not null; current = current.InnerException)
                parts.Add(current.Message);

            return string.Join(" -> ", parts.Distinct());
        }

        /// <summary>
        /// One ISP request for a batch of ASINs via Amazon search:
        /// <c>/s?k=ASIN1|ASIN2|...|ASIN48</c>. Round-robins ports on captcha/block.
        /// </summary>
        private async Task<List<OrganicProduct>> FetchAsinBatchSearchViaIspAsync(
            IReadOnlyList<string> asins, string domain, int batchIndex, CancellationToken ct)
        {
            if (asins.Count < 2)
                throw new InvalidOperationException("ASIN batch search requires at least 2 ASINs per request");

            var searchUrl = BuildAsinBatchSearchUrl(domain, asins);
            var triedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var maxAttempts = Math.Clamp(
                _asinRecheck.MaxAttemptsPerBatch, 1, Math.Max(1, _ispProxyRoundRobin.AvailablePortCount));
            Exception? lastError = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var endpoint = _ispProxyRoundRobin.Next(triedKeys);
                triedKeys.Add(endpoint.Key);
                var proxyPort = endpoint.UseProxy ? endpoint.Port : (int?)null;
                using var client = _ispProxyRoundRobin.CreateClient(endpoint);

                if (attempt > 1)
                    await Task.Delay(AmazonBrowserProfile.AfterIpSwitchDelayMs(), ct);

                try
                {
                    var organic = await FetchAsinBatchSearchPageAsync(
                        client, searchUrl, asins.Count, batchIndex, endpoint, proxyPort, ct);

                    _ispProxyRoundRobin.MarkHealthy(endpoint);
                    _logger.LogInformation(
                        "ASIN batch {Index}: OK via {Proxy} — {Returned}/{Requested} organic",
                        batchIndex, endpoint.Describe(), organic.Count, asins.Count);

                    return organic;
                }
                catch (AmazonFetchRejectedException ex)
                {
                    lastError = ex;

                    if (ex.IsTransport)
                        _ispProxyRoundRobin.MarkTransient(endpoint, ex.Message);
                    else
                        _ispProxyRoundRobin.MarkBad(endpoint, ex.Message);

                    _logger.LogWarning(
                        "ASIN batch {Index}: {Kind} on {Proxy} ({Reason}); {Left} ports left",
                        batchIndex, ex.IsTransport ? "connection failed" : "rejected",
                        endpoint.Describe(), ex.Message, maxAttempts - attempt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    var reason = DescribeTransport(ex);
                    _ispProxyRoundRobin.MarkTransient(endpoint, reason);
                    QueueLog(null, batchIndex, DateTime.UtcNow, 0, "TransportError",
                        $"{endpoint.Describe()} batch={batchIndex} asins={asins.Count}",
                        Truncate(reason, 2000), proxyPort);

                    _logger.LogWarning(ex,
                        "ASIN batch {Index}: transport error on {Proxy} ({Reason}); {Left} ports left",
                        batchIndex, endpoint.Describe(), reason, maxAttempts - attempt);
                }
            }

            throw new InvalidOperationException(
                $"ASIN batch {batchIndex} failed on all proxy ports ({asins.Count} ASINs)", lastError);
        }

        private async Task<List<OrganicProduct>> FetchAsinBatchSearchPageAsync(
            HttpClient client,
            string searchUrl,
            int requestedCount,
            int batchIndex,
            IspProxyEndpoint endpoint,
            int? proxyPort,
            CancellationToken ct)
        {
            var home = $"{new Uri(searchUrl).GetLeftPart(UriPartial.Authority)}/";

            // Amazon serves the "عذرًا!" soft-block page to sessions that jump straight to /s with no
            // cookies, so land on the homepage first and carry its cookies into the search.
            await WarmupAmazonHomeAsync(client, home, endpoint, $"ASIN batch {batchIndex}", ct);

            var requestedAt = DateTime.UtcNow;
            var requestLog = $"{endpoint.Describe()} GET {searchUrl}";

            HttpResponseMessage response;
            try
            {
                response = await SendNavigationAsync(
                    client, searchUrl, home, $"ASIN batch {batchIndex}", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var reason = DescribeTransport(ex);
                QueueLog(null, batchIndex, requestedAt, 0, "TransportError", requestLog, reason, proxyPort);
                throw new AmazonFetchRejectedException(
                    $"Transport error on ASIN batch {batchIndex}: {reason}",
                    statusCode: 0, ex, isTransport: true);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(ct);
                QueueLog(null, batchIndex, requestedAt, status, response.ReasonPhrase, requestLog,
                    status == 200 ? null : Truncate(body, 8000), proxyPort);

                if (status is 403 or 429 or 503 or 502 or 500)
                    throw new AmazonFetchRejectedException(
                        $"Blocked/unavailable status {status} on ASIN batch {batchIndex}", status);

                if (status != 200)
                    throw new AmazonFetchRejectedException(
                        $"HTTP {status} on ASIN batch {batchIndex}", status);

                if (string.IsNullOrWhiteSpace(body))
                    throw new AmazonFetchRejectedException(
                        $"Empty HTML on ASIN batch {batchIndex}", status);

                if (LooksLikeBotChallenge(body))
                    throw new AmazonFetchRejectedException(
                        $"Captcha/bot challenge on ASIN batch {batchIndex}", status);

                if (body.Length < 8_000)
                    throw new AmazonFetchRejectedException(
                        $"Suspiciously short HTML ({body.Length} chars) on ASIN batch {batchIndex}", status);

                if (!LooksLikeAmazonSearchHtml(body))
                    throw new AmazonFetchRejectedException(
                        $"Unexpected HTML (not a search results page) on ASIN batch {batchIndex}", status);

                var parsed = AmazonSearchHtmlParser.Parse(body);

                if (parsed.Organic.Count == 0 && !LooksLikeZeroResultsPage(body))
                    throw new AmazonFetchRejectedException(
                        $"No products parsed on ASIN batch {batchIndex} (requested {requestedCount})", status);

                return parsed.Organic;
            }
        }

        /// <summary>Builds <c>https://www.amazon.{domain}/s?k=ASIN1|ASIN2|...</c>.</summary>
        private static string BuildAsinBatchSearchUrl(string domain, IReadOnlyList<string> asins)
        {
            var query = string.Join("|", asins);
            var encoded = Uri.EscapeDataString(query);
            return $"https://www.amazon.{domain.Trim()}/s?k={encoded}&ref=nb_sb_noss";
        }

        /// <summary>
        /// Buffers audit rows instead of touching the DbContext, so parallel batch workers can log safely.
        /// Drained by <see cref="SaveAsync"/> on the calling thread.
        /// </summary>
        private void QueueLog(int? scraperUrlId, int page, DateTime requestedAt, int statusCode,
            string? statusPhrase, string requestBody, string? responseBody, int? proxyPort = null)
        {
            _pendingLogs.Enqueue(new OxylabsRequestLog
            {
                ScraperUrlId = scraperUrlId,
                Page = page,
                Port = proxyPort,
                RequestedAt = requestedAt,
                StatusCode = statusCode,
                StatusPhrase = Truncate(statusPhrase, 64),
                RequestBody = requestBody,
                ResponseBody = responseBody
            });
        }

        /// <summary>Flushes buffered request logs, then commits. Must only be called from the owning thread.</summary>
        private async Task<int> SaveAsync(CancellationToken ct)
        {
            while (_pendingLogs.TryDequeue(out var log))
                _db.OxylabsRequestLogs.Add(log);

            return await _db.SaveChangesAsync(ct);
        }

        private async Task MarkRunAsync(ScraperUrl scraperUrl, string? error, CancellationToken ct)
        {
            scraperUrl.LastRunAt = DateTime.UtcNow;
            scraperUrl.LastRunError = Truncate(error, 2000);
            await SaveAsync(ct);
        }

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

            await SaveAsync(ct);
            return products.Count;
        }

        private async Task<int> SaveProductsAsync(
            List<OrganicProduct> products,
            string domain,
            CancellationToken ct,
            int? scraperUrlId = null)
        {
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

                if (!isNew && product!.IsBlocked == true)
                    continue;

                var previousPrice = product?.CurrentPrice;
                var priceUnchanged = !isNew && previousPrice == org.Price;

                if (isNew)
                {
                    product = new Product { Asin = asin, CreatedAt = checkedAt };
                    _db.Products.Add(product);
                    existing[asin] = product;
                }

                if (scraperUrlId.HasValue)
                    product!.ScraperUrlId = scraperUrlId;

                ApplyOrganicData(product!, org, domain, checkedAt, isNew);

                if (!priceUnchanged)
                    product!.PriceHistory.Add(CreatePriceHistory(org, checkedAt));

                ApplyPriceTracking(product!, org.Price, previousPrice, isNew, alerts);
                saved++;
            }

            if (saved == 0 && alerts.Count == 0)
                return 0;

            await SaveAsync(ct);

            if (alerts.Count > 0)
            {
                await AttachPriceHistoryAsync(alerts, ct);
                if (await DispatchAlertsAsync(alerts, ct))
                    await SaveAsync(ct);
            }

            return saved;
        }

        private async Task AttachPriceHistoryAsync(List<ProductDropAlert> alerts, CancellationToken ct)
        {
            var productIds = alerts.Select(a => a.Product.Id).Distinct().ToList();
            if (productIds.Count == 0)
                return;

            var points = await _db.PriceHistories.AsNoTracking()
                .Where(h => productIds.Contains(h.ProductId) && h.Price != null)
                .OrderBy(h => h.CheckedAt)
                .ThenBy(h => h.Id)
                .Select(h => new { h.ProductId, h.CheckedAt, h.Price })
                .ToListAsync(ct);

            var byProduct = points
                .GroupBy(p => p.ProductId)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<PriceHistoryPoint>)g
                        .Select(p => new PriceHistoryPoint(string.Empty, p.Price, p.CheckedAt))
                        .ToList());

            foreach (var alert in alerts)
            {
                if (byProduct.TryGetValue(alert.Product.Id, out var history))
                    alert.History = history;
            }
        }

        private static void ApplyPriceTracking(
            Product product, decimal? newPrice, decimal? previousPrice, bool isNew, List<ProductDropAlert> alerts)
        {
            product.DropPercent = null;

            if (newPrice is not > 0)
                return;

            if (product.DropBaselinePrice is not > 0)
                product.DropBaselinePrice = previousPrice is > 0 ? previousPrice : newPrice;

            if (product.DropBaselinePrice is > 0 &&
                TryPercentOff(product.DropBaselinePrice.Value, newPrice.Value, out var dropPct))
                product.DropPercent = RoundPct(dropPct);

            if (isNew)
                return;

            if (previousPrice is not > 0)
                return;

            if (!TryPercentOff(previousPrice.Value, newPrice.Value, out var dropFromLast))
                return;

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

        /// <summary>Sets or replaces the <c>page</c> query parameter on an Amazon search URL.</summary>
        internal static string BuildPageUrl(string baseUrl, int page)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("URL is required.", nameof(baseUrl));

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                throw new InvalidOperationException($"Invalid scrape URL: {baseUrl}");

            var parts = new List<string>();
            var pageSet = false;
            var raw = uri.Query.TrimStart('?');
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (var segment in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = segment.IndexOf('=');
                    var key = eq >= 0 ? segment[..eq] : segment;
                    if (key.Equals("page", StringComparison.OrdinalIgnoreCase))
                    {
                        parts.Add($"page={page}");
                        pageSet = true;
                    }
                    else
                        parts.Add(segment);
                }
            }

            if (!pageSet)
                parts.Add($"page={page}");

            var builder = new UriBuilder(uri) { Port = -1, Query = string.Join('&', parts) };
            return builder.Uri.AbsoluteUri;
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
    }
}
