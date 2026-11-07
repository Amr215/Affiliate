using System.Collections.Concurrent;
using Affiliate.Data;
using Affiliate.Models.Dtos;
using Affiliate.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    public interface IArabicNameFillService
    {
        Task ProcessAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Loads unblocked products with null <c>NameAr</c>, fetches Amazon Arabic search pages
    /// (48 ASINs per request) through ISP proxies, and writes only Arabic titles.
    /// Does not touch prices, availability, English names, or other scrapers.
    /// </summary>
    public sealed class ArabicNameFillService(
        AffiliateDbContext db,
        IIspProxyService ispProxy,
        IOptions<ArabicNameFillOptions> options,
        ILogger<ArabicNameFillService> logger) : IArabicNameFillService
    {
        private const int MaxPagesPerSearch = 50;
        private const string AcceptLanguageAr = "ar-AE,ar;q=0.9,en-AE;q=0.5,en;q=0.4";

        private readonly ArabicNameFillOptions _options = options.Value;

        public async Task ProcessAsync(CancellationToken ct = default)
        {
            if (!_options.Enabled)
            {
                logger.LogDebug("Arabic name fill skipped (Enabled=false)");
                return;
            }

            var batchSize = Math.Clamp(_options.BatchSize, 2, 200);
            var maxProducts = Math.Max(batchSize, _options.MaxProductsPerRun);

            var asins = await db.Products.AsNoTracking()
                .Where(p => p.IsBlocked != true
                    && p.Asin != null && p.Asin != ""
                    && (p.NameAr == null || p.NameAr == ""))
                .OrderBy(p => p.Id)
                .Take(maxProducts)
                .Select(p => p.Asin!)
                .ToListAsync(ct);

            if (asins.Count == 0)
            {
                logger.LogInformation("Arabic name fill: no products need NameAr");
                return;
            }

            var batches = SplitIntoBatches(asins, batchSize);
            var parallel = Math.Clamp(_options.MaxParallelBatches, 1, Math.Max(1, batches.Count));

            logger.LogInformation(
                "Arabic name fill: {AsinCount} ASIN(s) in {BatchCount} batch(es), parallelism {Parallel}, domain={Domain}",
                asins.Count, batches.Count, parallel, _options.Domain);

            var titles = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var gate = new SemaphoreSlim(parallel);
            var tasks = batches.Select(async (batch, index) =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    await Task.Delay(AmazonBrowserProfile.BatchStaggerMs(index % parallel), ct);
                    var found = await FetchBatchTitlesAsync(batch, index + 1, ct);
                    foreach (var (asin, title) in found)
                        titles[asin] = title;
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks);

            var updated = await SaveTitlesAsync(titles, ct);
            logger.LogInformation(
                "Arabic name fill finished: fetched {Fetched} title(s), updated {Updated} product(s)",
                titles.Count, updated);
        }

        private async Task<Dictionary<string, string>> FetchBatchTitlesAsync(
            IReadOnlyList<string> asins, int batchIndex, CancellationToken ct)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (asins.Count < 2)
                return result;

            var domain = string.IsNullOrWhiteSpace(_options.Domain) ? "eg" : _options.Domain.Trim();
            var merchantId = _options.MerchantId?.Trim();
            var language = string.IsNullOrWhiteSpace(_options.AmazonLanguage)
                ? "ar_AE"
                : _options.AmazonLanguage.Trim();

            var requested = new HashSet<string>(asins, StringComparer.OrdinalIgnoreCase);

            var endpoint = ispProxy.GetEndpoint(allowBlocked: false);
            if (!endpoint.UseProxy)
            {
                logger.LogWarning("Arabic name batch {Index} skipped: no proxy (direct disabled)", batchIndex);
                return result;
            }

            var client = ispProxy.CreateClient(endpoint);
            ApplyArabicAcceptLanguage(client);

            string? referer = null;
            var warmed = false;
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
                            break;
                        await Task.Delay(AmazonBrowserProfile.NextPageDelayMs(), ct);
                    }

                    var pageOk = false;
                    string? nextHref = null;
                    var maxAttempts = Math.Max(1, _options.MaxAttemptsPerBatch);

                    for (var attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        var pageUrl = BuildArabicBatchSearchUrl(
                            domain, asins, merchantId, language, page, qid, xpid);

                        try
                        {
                            if (!warmed)
                            {
                                var home = $"https://www.amazon.{domain}/-/ar/?language={Uri.EscapeDataString(language)}";
                                await WarmupAsync(client, home, ct);
                                warmed = true;
                                referer = home;
                            }

                            var (organic, next) = await FetchPageAsync(
                                client, pageUrl, referer, batchIndex, page, ct);

                            foreach (var org in organic)
                            {
                                if (org.Asin is null || !requested.Contains(org.Asin))
                                    continue;
                                if (string.IsNullOrWhiteSpace(org.Title))
                                    continue;
                                result[org.Asin] = org.Title.Trim();
                            }

                            nextHref = next;
                            if (page == 1 && !string.IsNullOrWhiteSpace(nextHref)
                                && TryResolveHref(pageUrl, nextHref, out var absNext))
                            {
                                TryGetQueryParam(absNext, "qid", out qid);
                                TryGetQueryParam(absNext, "xpid", out xpid);
                            }

                            referer = pageUrl;
                            ispProxy.ReportSuccess(endpoint);
                            pageOk = true;
                            break;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            ispProxy.ReportFailure(endpoint);
                            logger.LogWarning(ex,
                                "Arabic name batch {Index} page {Page}: attempt {Attempt}/{Max} failed via {Proxy}",
                                batchIndex, page, attempt, maxAttempts, endpoint.Describe());

                            client.Dispose();
                            endpoint = ispProxy.GetEndpoint(allowBlocked: false);
                            if (!endpoint.UseProxy)
                            {
                                logger.LogWarning("Arabic name batch {Index} aborted: no proxy (direct disabled)", batchIndex);
                                return result;
                            }

                            client = ispProxy.CreateClient(endpoint);
                            ApplyArabicAcceptLanguage(client);
                            referer = null;
                            warmed = false;
                            await Task.Delay(AmazonBrowserProfile.AfterIpSwitchDelayMs(), ct);
                        }
                    }

                    if (!pageOk)
                        break;

                    if (result.Count >= requested.Count)
                        break;

                    if (string.IsNullOrWhiteSpace(nextHref))
                        break;
                }
            }
            finally
            {
                client.Dispose();
            }

            if (result.Count == 0)
            {
                logger.LogWarning("Arabic name batch {Index}: no titles parsed for {Count} ASIN(s)",
                    batchIndex, asins.Count);
            }

            return result;
        }

        private async Task<int> SaveTitlesAsync(IReadOnlyDictionary<string, string> titles, CancellationToken ct)
        {
            if (titles.Count == 0)
                return 0;

            var asinList = titles.Keys.ToList();
            var products = await db.Products
                .Where(p => p.Asin != null
                    && asinList.Contains(p.Asin)
                    && (p.NameAr == null || p.NameAr == "")
                    && p.IsBlocked != true)
                .ToListAsync(ct);

            var updated = 0;
            foreach (var product in products)
            {
                if (product.Asin is null || !titles.TryGetValue(product.Asin, out var nameAr))
                    continue;
                if (string.IsNullOrWhiteSpace(nameAr))
                    continue;

                product.NameAr = nameAr;
                updated++;
            }

            if (updated > 0)
                await db.SaveChangesAsync(ct);

            return updated;
        }

        private static List<List<string>> SplitIntoBatches(List<string> asins, int batchSize)
        {
            var batches = new List<List<string>>();
            for (var i = 0; i < asins.Count; i += batchSize)
                batches.Add(asins.GetRange(i, Math.Min(batchSize, asins.Count - i)));

            // Amazon search with a single k= term is unreliable for ASIN lookup — borrow one.
            if (batches.Count >= 2 && batches[^1].Count == 1)
            {
                var last = batches[^1][0];
                batches.RemoveAt(batches.Count - 1);
                batches[^1].Add(last);
            }
            else if (batches.Count == 1 && batches[0].Count == 1)
            {
                batches.Clear();
            }

            return batches;
        }

        private static string BuildArabicBatchSearchUrl(
            string domain,
            IReadOnlyList<string> asins,
            string? merchantId,
            string language,
            int page,
            string? qid,
            string? xpid)
        {
            var encoded = Uri.EscapeDataString(string.Join("|", asins));
            var rh = string.IsNullOrEmpty(merchantId)
                ? ""
                : $"&rh={Uri.EscapeDataString($"p_6:{merchantId}")}";
            var lang = $"&language={Uri.EscapeDataString(language)}";

            if (page <= 1)
                return $"https://www.amazon.{domain}/-/ar/s?k={encoded}&ref=nb_sb_noss{rh}{lang}";

            var xpidPart = string.IsNullOrWhiteSpace(xpid) ? "" : $"&xpid={Uri.EscapeDataString(xpid)}";
            var qidPart = string.IsNullOrWhiteSpace(qid) ? "" : $"&qid={Uri.EscapeDataString(qid)}";
            return $"https://www.amazon.{domain}/-/ar/s?k={encoded}{rh}{lang}&page={page}{xpidPart}{qidPart}&ref=sr_pg_{page}";
        }

        private static void ApplyArabicAcceptLanguage(HttpClient client)
        {
            client.DefaultRequestHeaders.Remove("Accept-Language");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", AcceptLanguageAr);
        }

        private async Task WarmupAsync(HttpClient client, string homeUrl, CancellationToken ct)
        {
            await Task.Delay(AmazonBrowserProfile.BeforeFirstRequestDelayMs(), ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, homeUrl);
            ApplyArabicNavigationHeaders(request, homeUrl, referer: null);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            _ = await response.Content.ReadAsStringAsync(ct);
        }

        private async Task<(List<OrganicProduct> Organic, string? NextHref)> FetchPageAsync(
            HttpClient client,
            string pageUrl,
            string? referer,
            int batchIndex,
            int page,
            CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            ApplyArabicNavigationHeaders(request, pageUrl, referer);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            var httpStatus = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct);

            if (httpStatus is 403 or 429 or 500 or 502 or 503)
                throw new InvalidOperationException($"Blocked/unavailable HTTP {httpStatus}");
            if (httpStatus != 200)
                throw new InvalidOperationException($"HTTP {httpStatus}");
            if (string.IsNullOrWhiteSpace(body) || body.Length < 8_000)
                throw new InvalidOperationException($"Suspiciously short HTML ({body?.Length ?? 0} chars)");
            if (LooksLikeBotChallenge(body))
                throw new InvalidOperationException("Captcha/bot challenge");
            if (!LooksLikeAmazonSearchHtml(body))
                throw new InvalidOperationException("Unexpected HTML (not a search results page)");

            var parsed = AmazonSearchHtmlParser.Parse(body);
            if (parsed.Organic.Count == 0 && !LooksLikeZeroResultsPage(body))
                throw new InvalidOperationException($"No products parsed on batch {batchIndex} page {page}");

            return (parsed.Organic, parsed.NextPageHref);
        }

        private static void ApplyArabicNavigationHeaders(HttpRequestMessage request, string pageUrl, string? referer)
        {
            AmazonBrowserProfile.ApplyNavigationHeaders(request, pageUrl, referer);
            request.Headers.Remove("Accept-Language");
            request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguageAr);
        }

        private static bool LooksLikeBotChallenge(string html) =>
            html.Contains("api-services-support@amazon.com", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Enter the characters you see below", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Type the characters you see in this image", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Robot Check", StringComparison.OrdinalIgnoreCase)
            || html.Contains("/errors/validateCaptcha", StringComparison.OrdinalIgnoreCase)
            || html.Contains("opfcaptcha.amazon", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeAmazonSearchHtml(string html) =>
            html.Contains("s-search-result", StringComparison.OrdinalIgnoreCase)
            || html.Contains("data-component-type=\"s-search-result\"", StringComparison.OrdinalIgnoreCase)
            || html.Contains("s-main-slot", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeZeroResultsPage(string html) =>
            html.Contains("no results for", StringComparison.OrdinalIgnoreCase)
            || html.Contains("لم يتم العثور على نتائج", StringComparison.OrdinalIgnoreCase)
            || html.Contains("لا توجد نتائج", StringComparison.OrdinalIgnoreCase);

        private static bool TryResolveHref(string currentPageUrl, string? href, out string absoluteUrl)
        {
            absoluteUrl = string.Empty;
            if (string.IsNullOrWhiteSpace(href))
                return false;

            href = href.Trim();
            if (Uri.TryCreate(href, UriKind.Absolute, out var abs)
                && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
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
    }
}
