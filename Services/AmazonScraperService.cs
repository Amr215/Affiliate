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

        /// <summary>Re-checks available products in ASIN batches via Amazon search (all result pages, ISP).</summary>
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
        private readonly IIspProxyService _ispProxy;
        private readonly ILogger<AmazonScraperService> _logger;
        private readonly ConcurrentQueue<OxylabsRequestLog> _pendingLogs = new();
        /// <summary>Serializes DbContext use — EF contexts are not thread-safe, and ASIN batches fetch in parallel.</summary>
        private readonly SemaphoreSlim _dbGate = new(1, 1);

        public AmazonScraperService(
            AffiliateDbContext dbContext,
            IScraperRunCoordinator runCoordinator,
            ITelegramNotifier telegramNotifier,
            IOptions<AsinRecheckOptions> asinRecheckOptions,
            IIspProxyService ispProxy,
            ILogger<AmazonScraperService> logger)
        {
            _db = dbContext;
            _runCoordinator = runCoordinator;
            _telegram = telegramNotifier;
            _asinRecheck = asinRecheckOptions.Value;
            _ispProxy = ispProxy;
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
                var batchSize = Math.Clamp(_asinRecheck.BatchSize, 2, 200);
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
                    "ASIN recheck starting via ISP — {Count} ASINs in {Batches} search request(s) of up to {BatchSize}, {Parallel} in parallel (save+alert after each page)",
                    asins.Count, batches.Count, batchSize, maxParallel);

                var failedBatches = 0;
                var incompleteBatches = 0;
                var updated = 0;
                var unavailable = 0;

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
                            var (organic, complete, pageSaved) = await FetchAsinBatchSearchViaIspAsync(
                                batch, domain, index + 1, token);

                            Interlocked.Add(ref updated, pageSaved);

                            if (!complete)
                            {
                                Interlocked.Increment(ref incompleteBatches);
                                _logger.LogWarning(
                                    "ASIN recheck batch {Index}: pagination incomplete — leaving missing ASINs unchecked",
                                    index + 1);
                                return;
                            }

                            var returned = new HashSet<string>(
                                organic
                                    .Where(p => !string.IsNullOrWhiteSpace(p.Asin))
                                    .Select(p => p.Asin!),
                                StringComparer.OrdinalIgnoreCase);

                            var missing = batch
                                .Where(a => !returned.Contains(a))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            if (missing.Count > 0)
                            {
                                var marked = await RecordNotAvailableAsync(missing, token);
                                Interlocked.Add(ref unavailable, marked);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Interlocked.Increment(ref failedBatches);
                            _logger.LogError(ex,
                                "ASIN recheck batch failed ({Count} ASINs)", batch.Count);
                        }
                    });

                await FlushPendingLogsAsync(ct);

                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                _logger.LogInformation(
                    "ASIN recheck completed in {Seconds:0.0}s — {Asins} ASINs, updated={Updated}, recordedNotAvailable={Unavailable}, failedBatches={Failed}, incompleteBatches={Incomplete}",
                    elapsed.TotalSeconds, asins.Count, updated, unavailable, failedBatches, incompleteBatches);
            }
            finally
            {
                _runCoordinator.Release();
            }
        }

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

        /// <summary>Caps parallelism at configured MaxParallelBatches and batch count.</summary>
        private int ResolveParallelism(int batchCount)
        {
            var configured = Math.Max(1, _asinRecheck.MaxParallelBatches);
            return Math.Clamp(configured, 1, batchCount);
        }

        private async Task ExecuteUrlScrapeAsync(ScraperUrl scraperUrl, CancellationToken ct)
        {
            _logger.LogInformation(
                "Running URL scrape {Id} ({Name}): url={Url}, domain={Domain}",
                scraperUrl.Id, scraperUrl.Name, scraperUrl.Url, scraperUrl.Domain);

            try
            {
                var (found, saved) = await FetchAllPagesAsync(scraperUrl, ct);

                if (found == 0)
                {
                    await MarkRunAsync(scraperUrl, "No organic products parsed from HTML", ct);
                    _logger.LogWarning("URL scrape {Id}: no organic products", scraperUrl.Id);
                    return;
                }

                await MarkRunAsync(scraperUrl, error: null, ct);
                _logger.LogInformation(
                    "URL scrape {Id} completed — saved/updated {Saved} of {Total} products",
                    scraperUrl.Id, saved, found);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await MarkRunAsync(scraperUrl, ex.Message, ct);
                _logger.LogError(ex, "URL scrape {Id} failed", scraperUrl.Id);
            }
        }

        /// <summary>
        /// Fetches pages starting at <see cref="ScraperUrl.StartPage"/> up to the last visible page.
        /// Saves products (and sends drop alerts) after every successful page request.
        /// </summary>
        private async Task<(int Found, int Saved)> FetchAllPagesAsync(ScraperUrl scraperUrl, CancellationToken ct)
        {
            var found = 0;
            var saved = 0;
            var firstPage = Math.Max(1, scraperUrl.StartPage);
            var lastPage = firstPage;

            var endpoint = _ispProxy.GetEndpoint();
            var client = _ispProxy.CreateClient(endpoint);
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

                    try
                    {
                        if (!warmedHost)
                        {
                            await WarmupAmazonHomeAsync(client, scraperUrl, endpoint, ct);
                            warmedHost = true;
                        }

                        var (result, pageUrl) = await FetchSearchPageAsync(
                            client, scraperUrl, page, referer, endpoint, proxyPort, ct);

                        found += result.Organic.Count;
                        lastPage = result.LastVisiblePage ?? lastPage;
                        referer = pageUrl;

                        if (result.Organic.Count > 0)
                        {
                            var pageSaved = await SaveProductsAsync(
                                result.Organic, scraperUrl.Domain, ct, scraperUrl.Id);
                            saved += pageSaved;
                        }

                        _logger.LogInformation(
                            "URL scrape {Id}: page {Page}/{Last} via {Proxy} — {Count} organic, saved={Saved}",
                            scraperUrl.Id, page, lastPage, endpoint.Describe(), result.Organic.Count, saved);
                    }
                    catch (AmazonFetchRejectedException ex)
                    {
                        // Already logged once in FetchSearchPageAsync — do not write a second OxylabsRequestLog row.
                        if (found == 0)
                            throw;

                        _logger.LogWarning(
                            "URL scrape {Id}: giving up page {Page} ({Reason}); keeping {Count} products already saved",
                            scraperUrl.Id, page, ex.Message, found);
                        return (found, saved);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not AmazonFetchRejectedException)
                    {
                        QueueLog(scraperUrl.Id, page, DateTime.UtcNow, 0, "TransportError",
                            $"{endpoint.Describe()} page={page}", Truncate(DescribeTransport(ex), 2000), proxyPort);

                        if (found == 0)
                            throw;

                        _logger.LogWarning(ex,
                            "URL scrape {Id}: stopped at page {Page}; keeping {Count} products already saved",
                            scraperUrl.Id, page, found);
                        return (found, saved);
                    }

                    if (page >= lastPage)
                        break;
                }

                return (found, saved);
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
                var httpStatus = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(ct);

                string? rejectReason = null;
                var logStatus = httpStatus;
                if (httpStatus is 403 or 429 or 503 or 502 or 500)
                    rejectReason = $"Blocked/unavailable status {httpStatus} on page {page}";
                else if (httpStatus != 200)
                    rejectReason = $"HTTP {httpStatus} on page {page}";
                else if (string.IsNullOrWhiteSpace(body))
                    rejectReason = $"Empty HTML on page {page}";
                else if (LooksLikeBotChallenge(body))
                {
                    // Amazon/Akamai often returns challenges with HTTP 200 — treat as failed block.
                    logStatus = SoftBlockLoggedStatusCode;
                    rejectReason = $"Captcha/bot challenge on page {page}";
                }
                else if (body.Length < 8_000)
                    rejectReason = $"Suspiciously short HTML ({body.Length} chars) on page {page}";
                else if (!LooksLikeAmazonSearchHtml(body))
                    rejectReason = $"Unexpected HTML (not a search results page) on page {page}";

                SearchPageParseResult? parsed = null;
                if (rejectReason is null)
                {
                    parsed = AmazonSearchHtmlParser.Parse(body);
                    if (parsed.Organic.Count == 0 && !LooksLikeZeroResultsPage(body))
                        rejectReason = $"No products parsed and page does not look like zero-results on page {page}";
                }

                if (rejectReason is not null)
                {
                    // Always persist Amazon's HTML (when present) so it can be inspected/parsed later.
                    // Reject reason stays in the exception / app logs only — never in ResponseBody.
                    QueueLog(scraperUrl.Id, page, requestedAt, logStatus, "Rejected", requestLog,
                        string.IsNullOrEmpty(body) ? null : body, proxyPort);
                    throw new AmazonFetchRejectedException(rejectReason, logStatus);
                }

                QueueLog(scraperUrl.Id, page, requestedAt, httpStatus, response.ReasonPhrase, requestLog,
                    null, proxyPort);
                return (parsed!, pageUrl);
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
                || LooksLikeAkamaiInterstitial(html)
                || LooksLikeSoftBlockPage(html);
        }

        /// <summary>
        /// Akamai bot interstitial Amazon often returns with HTTP 200 (meta refresh + bm-verify + /_sec/verify).
        /// Must be treated as a block, not a successful page.
        /// </summary>
        private static bool LooksLikeAkamaiInterstitial(string html) =>
            html.Contains("bm-verify", StringComparison.OrdinalIgnoreCase)
            || html.Contains("triggerInterstitialChallenge", StringComparison.OrdinalIgnoreCase)
            || html.Contains("/_sec/verify", StringComparison.OrdinalIgnoreCase)
            || html.Contains("provider=interstitial", StringComparison.OrdinalIgnoreCase);

        /// <summary>Amazon's throttling "Sorry! / عذرًا!" page, served with either 200 or 503.</summary>
        private static bool LooksLikeSoftBlockPage(string html) =>
            html.Contains("ref=cs_503", StringComparison.OrdinalIgnoreCase)
            || html.Contains("cs_503_logo", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Sorry! Something went wrong", StringComparison.OrdinalIgnoreCase)
            || html.Contains("communities/people/logo.gif", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Amazon sometimes serves blocks/challenges with HTTP 200. Log/retry those as 403 so they
        /// are not treated as successful responses.
        /// </summary>
        private const int SoftBlockLoggedStatusCode = 403;

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
            var retries = _ispProxy.TransportRetriesPerIp;

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
        /// Fetches every search-results page for a batch of ASINs using Amazon's real URL shape:
        /// page 1 <c>/s?k=…&amp;ref=nb_sb_noss</c>, then page N
        /// <c>/-/en/s?k=…&amp;page=N&amp;xpid=…&amp;qid=…&amp;ref=sr_pg_N</c>.
        /// Saves products (and sends drop alerts) after every successful page request.
        /// </summary>
        private async Task<(List<OrganicProduct> Organic, bool IsComplete, int Saved)> FetchAsinBatchSearchViaIspAsync(
            IReadOnlyList<string> asins, string domain, int batchIndex, CancellationToken ct)
        {
            if (asins.Count < 2)
                throw new InvalidOperationException("ASIN batch search requires at least 2 ASINs per request");

            var merchantId = _asinRecheck.MerchantId;
            var requested = new HashSet<string>(asins, StringComparer.OrdinalIgnoreCase);
            var all = new List<OrganicProduct>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var saved = 0;

            var endpoint = _ispProxy.GetEndpoint();
            var client = _ispProxy.CreateClient(endpoint);
            string? referer = null;
            var warmedHost = false;
            int? proxyPort = endpoint.UseProxy ? endpoint.Port : null;
            string? qid = null;
            string? xpid = null;

            try
            {
                for (var page = 1; page <= MaxPagesPerSearch; page++)
                {
                    ct.ThrowIfCancellationRequested();

                    if (page > 1)
                    {
                        if (string.IsNullOrWhiteSpace(qid))
                        {
                            _logger.LogWarning(
                                "ASIN batch {Index}: missing qid after page 1 — cannot build page {Page}",
                                batchIndex, page);
                            return (all, IsComplete: false, saved);
                        }

                        await Task.Delay(AmazonBrowserProfile.NextPageDelayMs(), ct);
                    }

                    var pageUrl = BuildAsinBatchSearchUrl(domain, asins, merchantId, page, qid, xpid);

                    var pageOk = false;
                    var pageOrganicCount = 0;
                    string? nextHref = null;
                    var lastVisible = page;
                    var newOnPage = 0;
                    var maxAttempts = Math.Max(1, _asinRecheck.MaxAttemptsPerBatch);

                    for (var attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        try
                        {
                            if (!warmedHost)
                            {
                                var home = $"https://www.amazon.{domain.Trim()}/";
                                await WarmupAmazonHomeAsync(
                                    client, home, endpoint, $"ASIN batch {batchIndex}", ct);
                                warmedHost = true;
                                referer = home;
                            }

                            var result = await FetchAsinBatchSearchPageAsync(
                                client, pageUrl, referer, asins.Count, batchIndex, page,
                                endpoint, proxyPort, ct);

                            newOnPage = 0;
                            foreach (var org in result.Organic)
                            {
                                if (string.IsNullOrWhiteSpace(org.Asin) || !seen.Add(org.Asin))
                                    continue;
                                all.Add(org);
                                newOnPage++;
                            }

                            pageOrganicCount = result.Organic.Count;
                            lastVisible = result.LastVisiblePage ?? lastVisible;
                            nextHref = result.NextPageHref;
                            referer = pageUrl;
                            pageOk = true;

                            if (qid is null &&
                                TryResolveAmazonHref(pageUrl, nextHref, out var nextAbs) &&
                                TryGetQueryParam(nextAbs, "qid", out var parsedQid))
                            {
                                qid = parsedQid;
                                TryGetQueryParam(nextAbs, "xpid", out xpid);
                            }

                            if (result.Organic.Count > 0)
                            {
                                var pageSaved = await SaveProductsAsync(result.Organic, domain, ct);
                                saved += pageSaved;
                            }

                            _logger.LogInformation(
                                "ASIN batch {Index}: Amazon page {Page}/{Last} via {Proxy} — {PageCount} organic, {New} new (total {Total}/{Requested}, saved={Saved}, hasNext={HasNext}) GET {Url}",
                                batchIndex, page, lastVisible, endpoint.Describe(),
                                pageOrganicCount, newOnPage, all.Count, asins.Count, saved, result.HasNextPage,
                                Truncate(pageUrl, 180));
                            break;
                        }
                        catch (AmazonFetchRejectedException ex)
                        {
                            // Already logged once in FetchAsinBatchSearchPageAsync — do not write a second OxylabsRequestLog row.
                            if (attempt >= maxAttempts)
                            {
                                if (all.Count == 0)
                                    throw;

                                _logger.LogWarning(
                                    "ASIN batch {Index}: giving up page {Page} after {Attempts} attempts; keeping {Count} products already saved (incomplete)",
                                    batchIndex, page, attempt, all.Count);
                                return (all, IsComplete: false, saved);
                            }

                            client.Dispose();
                            client = _ispProxy.CreateClient(endpoint);
                            referer = null;
                            warmedHost = false;

                            _logger.LogWarning(
                                "ASIN batch {Index}: page {Page} {Kind} ({Reason}); retrying via {Proxy} (attempt {Attempt}/{Max})",
                                batchIndex, page, ex.IsTransport ? "connection failed" : "rejected",
                                ex.Message, endpoint.Describe(), attempt + 1, maxAttempts);

                            await Task.Delay(AmazonBrowserProfile.AfterIpSwitchDelayMs(), ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException and not AmazonFetchRejectedException)
                        {
                            var reason = DescribeTransport(ex);
                            QueueLog(null, page, DateTime.UtcNow, 0, "TransportError",
                                $"{endpoint.Describe()} batch={batchIndex} page={page} asins={asins.Count}",
                                Truncate(reason, 2000), proxyPort);

                            if (attempt >= maxAttempts)
                            {
                                if (all.Count == 0)
                                    throw;

                                _logger.LogWarning(ex,
                                    "ASIN batch {Index}: stopped at page {Page}; keeping {Count} products already saved (incomplete)",
                                    batchIndex, page, all.Count);
                                return (all, IsComplete: false, saved);
                            }

                            client.Dispose();
                            client = _ispProxy.CreateClient(endpoint);
                            referer = null;
                            warmedHost = false;

                            _logger.LogWarning(ex,
                                "ASIN batch {Index}: transport error on page {Page}; retrying via {Proxy} (attempt {Attempt}/{Max})",
                                batchIndex, page, endpoint.Describe(), attempt + 1, maxAttempts);

                            await Task.Delay(AmazonBrowserProfile.AfterIpSwitchDelayMs(), ct);
                        }
                    }

                    if (!pageOk)
                        return (all, IsComplete: false, saved);

                    var matched = all.Count(p => p.Asin is not null && requested.Contains(p.Asin));
                    if (matched >= asins.Count)
                        return (all, IsComplete: true, saved);

                    if (pageOrganicCount > 0 && newOnPage == 0)
                    {
                        _logger.LogWarning(
                            "ASIN batch {Index}: page {Page} returned only duplicate ASINs — stopping as incomplete",
                            batchIndex, page);
                        return (all, IsComplete: false, saved);
                    }

                    if (string.IsNullOrWhiteSpace(nextHref))
                        return (all, IsComplete: true, saved);
                }

                return (all, IsComplete: false, saved);
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <summary>Resolves a relative Amazon pagination href against the current page URL.</summary>
        private static bool TryResolveAmazonHref(string currentPageUrl, string? href, out string absoluteUrl)
        {
            absoluteUrl = string.Empty;
            if (string.IsNullOrWhiteSpace(href))
                return false;

            href = href.Trim();
            if (Uri.TryCreate(href, UriKind.Absolute, out var abs) &&
                (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            {
                absoluteUrl = abs.AbsoluteUri;
                return true;
            }

            if (!Uri.TryCreate(currentPageUrl, UriKind.Absolute, out var baseUri))
                return false;

            if (!Uri.TryCreate(baseUri, href, out var resolved))
                return false;

            absoluteUrl = resolved.AbsoluteUri;
            return true;
        }

        private static bool TryGetQueryParam(string url, string key, out string? value)
        {
            value = null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            foreach (var segment in uri.Query.TrimStart('?')
                         .Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = segment.IndexOf('=');
                var segmentKey = eq >= 0 ? segment[..eq] : segment;
                if (!segmentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;

                value = eq >= 0 ? Uri.UnescapeDataString(segment[(eq + 1)..]) : "";
                return !string.IsNullOrWhiteSpace(value);
            }

            return false;
        }

        private async Task<SearchPageParseResult> FetchAsinBatchSearchPageAsync(
            HttpClient client,
            string pageUrl,
            string? referer,
            int requestedCount,
            int batchIndex,
            int page,
            IspProxyEndpoint endpoint,
            int? proxyPort,
            CancellationToken ct)
        {
            var requestedAt = DateTime.UtcNow;
            var requestLog = $"{endpoint.Describe()} GET {pageUrl}";

            HttpResponseMessage response;
            try
            {
                response = await SendNavigationAsync(
                    client, pageUrl, referer, $"ASIN batch {batchIndex} page {page}", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var reason = DescribeTransport(ex);
                QueueLog(null, page, requestedAt, 0, "TransportError", requestLog, reason, proxyPort);
                throw new AmazonFetchRejectedException(
                    $"Transport error on ASIN batch {batchIndex} page {page}: {reason}",
                    statusCode: 0, ex, isTransport: true);
            }

            using (response)
            {
                var httpStatus = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(ct);

                string? rejectReason = null;
                var logStatus = httpStatus;
                if (httpStatus is 403 or 429 or 503 or 502 or 500)
                    rejectReason = $"Blocked/unavailable status {httpStatus} on ASIN batch {batchIndex} page {page}";
                else if (httpStatus != 200)
                    rejectReason = $"HTTP {httpStatus} on ASIN batch {batchIndex} page {page}";
                else if (string.IsNullOrWhiteSpace(body))
                    rejectReason = $"Empty HTML on ASIN batch {batchIndex} page {page}";
                else if (LooksLikeBotChallenge(body))
                {
                    // Amazon/Akamai often returns challenges with HTTP 200 — treat as failed block.
                    logStatus = SoftBlockLoggedStatusCode;
                    rejectReason = $"Captcha/bot challenge on ASIN batch {batchIndex} page {page}";
                }
                else if (body.Length < 8_000)
                    rejectReason = $"Suspiciously short HTML ({body.Length} chars) on ASIN batch {batchIndex} page {page}";
                else if (!LooksLikeAmazonSearchHtml(body))
                    rejectReason = $"Unexpected HTML (not a search results page) on ASIN batch {batchIndex} page {page}";

                SearchPageParseResult? parsed = null;
                if (rejectReason is null)
                {
                    parsed = AmazonSearchHtmlParser.Parse(body);
                    if (parsed.Organic.Count == 0 && !LooksLikeZeroResultsPage(body))
                        rejectReason =
                            $"No products parsed on ASIN batch {batchIndex} page {page} (requested {requestedCount})";
                }

                if (rejectReason is not null)
                {
                    // Always persist Amazon's HTML (when present) so it can be inspected/parsed later.
                    // Reject reason stays in the exception / app logs only — never in ResponseBody.
                    QueueLog(null, page, requestedAt, logStatus, "Rejected", requestLog,
                        string.IsNullOrEmpty(body) ? null : body, proxyPort);
                    throw new AmazonFetchRejectedException(rejectReason, logStatus);
                }

                QueueLog(null, page, requestedAt, httpStatus, response.ReasonPhrase, requestLog, null, proxyPort);
                return parsed!;
            }
        }

        /// <summary>
        /// Builds ASIN batch search URLs in Amazon's browser shape:
        /// page 1 <c>https://www.amazon.{domain}/s?k=…&amp;ref=nb_sb_noss</c>,
        /// page N <c>https://www.amazon.{domain}/-/en/s?k=…&amp;page=N&amp;xpid=…&amp;qid=…&amp;ref=sr_pg_N</c>.
        /// Optional <c>rh=p_6:MERCHANT</c> when <paramref name="merchantId"/> is set.
        /// </summary>
        private static string BuildAsinBatchSearchUrl(
            string domain,
            IReadOnlyList<string> asins,
            string? merchantId,
            int page = 1,
            string? qid = null,
            string? xpid = null)
        {
            var encoded = Uri.EscapeDataString(string.Join("|", asins));
            var d = domain.Trim();
            var rh = string.IsNullOrWhiteSpace(merchantId)
                ? ""
                : $"&rh={Uri.EscapeDataString($"p_6:{merchantId.Trim()}")}";

            if (page <= 1)
                return $"https://www.amazon.{d}/s?k={encoded}&ref=nb_sb_noss{rh}";

            // Match browser pagination: /-/en/s + page=N + xpid + qid + ref=sr_pg_N
            var xpidPart = string.IsNullOrWhiteSpace(xpid) ? "" : $"&xpid={Uri.EscapeDataString(xpid)}";
            var qidPart = string.IsNullOrWhiteSpace(qid) ? "" : $"&qid={Uri.EscapeDataString(qid)}";
            return $"https://www.amazon.{d}/-/en/s?k={encoded}{rh}&page={page}{xpidPart}{qidPart}&ref=sr_pg_{page}";
        }

        /// <summary>
        /// Buffers audit rows instead of touching the DbContext, so parallel batch workers can log safely.
        /// Drained by <see cref="SaveChangesCoreAsync"/> under the DB lock.
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

        /// <summary>Flushes buffered request logs, then commits. Caller must hold <see cref="_dbGate"/>.</summary>
        private async Task<int> SaveChangesCoreAsync(CancellationToken ct)
        {
            while (_pendingLogs.TryDequeue(out var log))
                _db.OxylabsRequestLogs.Add(log);

            return await _db.SaveChangesAsync(ct);
        }

        private async Task FlushPendingLogsAsync(CancellationToken ct)
        {
            await _dbGate.WaitAsync(ct);
            try
            {
                await SaveChangesCoreAsync(ct);
                _db.ChangeTracker.Clear();
            }
            finally
            {
                _dbGate.Release();
            }
        }

        private async Task MarkRunAsync(ScraperUrl scraperUrl, string? error, CancellationToken ct)
        {
            await _dbGate.WaitAsync(ct);
            try
            {
                scraperUrl.LastRunAt = DateTime.UtcNow;
                scraperUrl.LastRunError = Truncate(error, 2000);

                // Per-page saves clear the change tracker, so the scheduled entity may be detached.
                var entry = _db.Entry(scraperUrl);
                if (entry.State == EntityState.Detached)
                {
                    _db.ScraperUrls.Attach(scraperUrl);
                    entry.Property(s => s.LastRunAt).IsModified = true;
                    entry.Property(s => s.LastRunError).IsModified = true;
                }

                await SaveChangesCoreAsync(ct);
                _db.ChangeTracker.Clear();
            }
            finally
            {
                _dbGate.Release();
            }
        }

        /// <summary>
        /// Marks products as not available when they were requested in a fully paginated ASIN
        /// batch search and never appeared in any page.
        /// </summary>
        private async Task<int> RecordNotAvailableAsync(IReadOnlyList<string> asins, CancellationToken ct)
        {
            if (asins.Count == 0)
                return 0;

            await _dbGate.WaitAsync(ct);
            try
            {
                var products = await _db.Products
                    .Where(p => p.Asin != null && asins.Contains(p.Asin) && p.IsBlocked != true)
                    .ToListAsync(ct);

                var checkedAt = DateTime.UtcNow;
                foreach (var product in products)
                {
                    product.LastCheckedAt = checkedAt;
                    product.NotAvailableDate ??= checkedAt;
                }

                await SaveChangesCoreAsync(ct);
                _db.ChangeTracker.Clear();
                return products.Count;
            }
            finally
            {
                _dbGate.Release();
            }
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

            await _dbGate.WaitAsync(ct);
            try
            {
                var asins = byAsin.Keys.ToList();
                var existing = await _db.Products
                    .Where(p => p.Asin != null && asins.Contains(p.Asin))
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

                await SaveChangesCoreAsync(ct);

                if (alerts.Count > 0)
                {
                    await AttachPriceHistoryAsync(alerts, ct);
                    if (await DispatchAlertsAsync(alerts, ct))
                        await SaveChangesCoreAsync(ct);
                }

                _db.ChangeTracker.Clear();
                return saved;
            }
            finally
            {
                _dbGate.Release();
            }
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

            const int historyEnds = 5;

            var byProduct = points
                .GroupBy(p => p.ProductId)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var all = g
                            .Select(p => new PriceHistoryPoint(string.Empty, p.Price, p.CheckedAt))
                            .ToList();
                        var priced = all.Where(p => p.Price.HasValue).Select(p => p.Price!.Value).ToList();
                        var average = priced.Count > 0 ? priced.Average() : (decimal?)null;
                        var truncated = all.Count > historyEnds * 2;
                        IReadOnlyList<PriceHistoryPoint> window = truncated
                            ? all.Take(historyEnds).Concat(all.TakeLast(historyEnds)).ToList()
                            : all;
                        return (History: window, Average: average, Truncated: truncated);
                    });

            foreach (var alert in alerts)
            {
                if (!byProduct.TryGetValue(alert.Product.Id, out var data))
                    continue;

                alert.History = data.History;
                alert.AveragePrice = data.Average.HasValue
                    ? Math.Round(data.Average.Value, 2, MidpointRounding.AwayFromZero)
                    : null;
                alert.HistoryTruncated = data.Truncated;
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
