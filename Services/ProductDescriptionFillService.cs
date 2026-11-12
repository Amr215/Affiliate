using Affiliate.Data;
using Affiliate.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    public interface IProductDescriptionFillService
    {
        Task ProcessAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Loads unblocked products missing <c>DescriptionAr</c> and/or <c>DescriptionEn</c>,
    /// fetches Amazon product detail pages through ISP proxies, and writes descriptions only.
    /// Saves each product as soon as it is fetched (does not wait for the whole batch).
    /// </summary>
    public sealed class ProductDescriptionFillService(
        AffiliateDbContext db,
        IServiceScopeFactory scopeFactory,
        IIspProxyService ispProxy,
        IOptions<ProductDescriptionFillOptions> options,
        ILogger<ProductDescriptionFillService> logger) : IProductDescriptionFillService
    {
        private const string AcceptLanguageAr = "ar-AE,ar;q=0.9,en-AE;q=0.5,en;q=0.4";
        private const string AcceptLanguageEn = "en-AE,en-US;q=0.9,en;q=0.8,ar-EG;q=0.5,ar;q=0.4";

        private readonly ProductDescriptionFillOptions _options = options.Value;

        public async Task ProcessAsync(CancellationToken ct = default)
        {
            if (!_options.Enabled)
            {
                logger.LogWarning("Product description fill skipped (Enabled=false)");
                return;
            }

            var probe = ispProxy.GetEndpoint(allowBlocked: false);
            if (!probe.UseProxy)
            {
                logger.LogError("Product description fill aborted: no ISP proxy available");
                return;
            }

            var maxProducts = Math.Clamp(_options.MaxProductsPerRun, 1, 25000);
            var maxLen = Math.Clamp(_options.MaxDescriptionLength, 200, 4000);

            var products = await db.Products.AsNoTracking()
                .Where(p => p.IsBlocked != true
                    && p.Asin != null && p.Asin != ""
                    && ((p.DescriptionAr == null || p.DescriptionAr == "")
                        || (p.DescriptionEn == null || p.DescriptionEn == "")))
                .OrderBy(p => p.Id)
                .Take(maxProducts)
                .Select(p => new WorkItem(
                    p.Id,
                    p.Asin!,
                    string.IsNullOrWhiteSpace(p.DescriptionAr),
                    string.IsNullOrWhiteSpace(p.DescriptionEn)))
                .ToListAsync(ct);

            if (products.Count == 0)
            {
                logger.LogWarning("Product description fill: no products need descriptions");
                return;
            }

            var parallel = Math.Clamp(_options.MaxParallel, 1, Math.Min(64, Math.Max(1, products.Count)));
            logger.LogInformation(
                "Product description fill starting: {Count} product(s), parallelism {Parallel}, domain={Domain}, via proxy {Proxy}",
                products.Count, parallel, _options.Domain, probe.Describe());

            var fetched = 0;
            var updated = 0;
            using var gate = new SemaphoreSlim(parallel);
            var staggerBase = Math.Clamp(_options.BatchStaggerMs, 0, 2000);
            var tasks = products.Select(async (item, index) =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (staggerBase > 0)
                    {
                        var stagger = staggerBase * (index % parallel) + Random.Shared.Next(0, staggerBase);
                        await Task.Delay(stagger, ct);
                    }

                    var (ar, en) = await FetchDescriptionsAsync(item, maxLen, ct);
                    if (ar is null && en is null)
                        return;

                    Interlocked.Increment(ref fetched);
                    var saved = await SaveOneAsync(item.Id, ar, en, ct);
                    if (saved)
                    {
                        var total = Interlocked.Increment(ref updated);
                        if (total <= 20 || total % 100 == 0)
                        {
                            logger.LogInformation(
                                "Product description saved {Asin} ({Total}/{Goal})",
                                item.Asin, total, products.Count);
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks);

            logger.LogInformation(
                "Product description fill finished: fetched {Fetched}, saved {Updated} of {Total} candidate(s)",
                fetched, updated, products.Count);
        }

        private async Task<(string? Ar, string? En)> FetchDescriptionsAsync(
            WorkItem item, int maxLen, CancellationToken ct)
        {
            string? ar = null;
            string? en = null;

            if (item.NeedAr)
                ar = await FetchOneLanguageAsync(item.Asin, arabic: true, maxLen, ct);

            if (item.NeedEn)
            {
                if (item.NeedAr)
                    await DelayBetweenLanguagesAsync(ct);
                en = await FetchOneLanguageAsync(item.Asin, arabic: false, maxLen, ct);
            }

            return (ar, en);
        }

        private async Task<string?> FetchOneLanguageAsync(
            string asin, bool arabic, int maxLen, CancellationToken ct)
        {
            var domain = string.IsNullOrWhiteSpace(_options.Domain) ? "eg" : _options.Domain.Trim();
            var language = arabic
                ? (string.IsNullOrWhiteSpace(_options.AmazonLanguageAr) ? "ar_AE" : _options.AmazonLanguageAr.Trim())
                : (string.IsNullOrWhiteSpace(_options.AmazonLanguageEn) ? "en_AE" : _options.AmazonLanguageEn.Trim());
            var acceptLanguage = arabic ? AcceptLanguageAr : AcceptLanguageEn;
            var pathLang = arabic ? "ar" : "en";
            var pageUrl =
                $"https://www.amazon.{domain}/-/{pathLang}/dp/{Uri.EscapeDataString(asin)}?language={Uri.EscapeDataString(language)}&th=1&psc=1";
            var home =
                $"https://www.amazon.{domain}/-/{pathLang}/?language={Uri.EscapeDataString(language)}";

            var maxAttempts = Math.Max(1, _options.MaxAttemptsPerPage);
            var endpoint = ispProxy.GetEndpoint(allowBlocked: false);
            if (!endpoint.UseProxy)
            {
                logger.LogWarning(
                    "Description fill {Asin} ({Lang}) skipped: no proxy",
                    asin, arabic ? "ar" : "en");
                return null;
            }

            var client = ispProxy.CreateClient(endpoint);
            ApplyAcceptLanguage(client, acceptLanguage);
            var warmed = false;
            string? referer = null;

            try
            {
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (!warmed && !_options.SkipHomeWarmup)
                        {
                            await WarmupAsync(client, home, acceptLanguage, ct);
                            warmed = true;
                            referer = home;
                        }
                        else if (!warmed)
                        {
                            warmed = true;
                        }

                        var html = await FetchHtmlAsync(client, pageUrl, referer, acceptLanguage, ct);
                        ispProxy.ReportSuccess(endpoint);

                        var description = AmazonProductDescriptionParser.Parse(html, maxLen);
                        if (string.IsNullOrWhiteSpace(description))
                        {
                            logger.LogWarning(
                                "Description fill {Asin} ({Lang}): page OK ({Chars} chars) but no description parsed",
                                asin, arabic ? "ar" : "en", html.Length);
                            return null;
                        }

                        logger.LogInformation(
                            "Description fill {Asin} ({Lang}): got {Len} char(s) via {Proxy}",
                            asin, arabic ? "ar" : "en", description.Length, endpoint.Describe());
                        return description;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        ispProxy.ReportFailure(endpoint);
                        logger.LogWarning(ex,
                            "Description fill {Asin} ({Lang}): attempt {Attempt}/{Max} failed via {Proxy}",
                            asin, arabic ? "ar" : "en", attempt, maxAttempts, endpoint.Describe());

                        client.Dispose();
                        endpoint = ispProxy.GetEndpoint(allowBlocked: false);
                        if (!endpoint.UseProxy)
                        {
                            logger.LogWarning(
                                "Description fill {Asin} ({Lang}) aborted: no proxy",
                                asin, arabic ? "ar" : "en");
                            return null;
                        }

                        client = ispProxy.CreateClient(endpoint);
                        ApplyAcceptLanguage(client, acceptLanguage);
                        referer = null;
                        warmed = false;
                        await Task.Delay(AmazonBrowserProfile.AfterIpSwitchDelayMs(), ct);
                    }
                }
            }
            finally
            {
                client.Dispose();
            }

            return null;
        }

        private async Task<bool> SaveOneAsync(int id, string? ar, string? en, CancellationToken ct)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<AffiliateDbContext>();
            var product = await scopedDb.Products
                .FirstOrDefaultAsync(p => p.Id == id && p.IsBlocked != true, ct);
            if (product is null)
                return false;

            var changed = false;
            if (!string.IsNullOrWhiteSpace(ar) && string.IsNullOrWhiteSpace(product.DescriptionAr))
            {
                product.DescriptionAr = ar;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(en) && string.IsNullOrWhiteSpace(product.DescriptionEn))
            {
                product.DescriptionEn = en;
                changed = true;
            }

            if (!changed)
                return false;

            await scopedDb.SaveChangesAsync(ct);
            return true;
        }

        private Task DelayBetweenLanguagesAsync(CancellationToken ct)
        {
            var ms = Math.Clamp(_options.DelayBetweenLanguagesMs, 0, 60_000);
            if (ms <= 0)
                return Task.CompletedTask;
            return Task.Delay(ms + Random.Shared.Next(0, Math.Min(150, ms + 1)), ct);
        }

        private static void ApplyAcceptLanguage(HttpClient client, string acceptLanguage)
        {
            client.DefaultRequestHeaders.Remove("Accept-Language");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }

        private async Task WarmupAsync(
            HttpClient client, string homeUrl, string acceptLanguage, CancellationToken ct)
        {
            var warmupMs = Math.Clamp(_options.WarmupDelayMs, 0, 15_000);
            if (warmupMs > 0)
                await Task.Delay(warmupMs + Random.Shared.Next(0, Math.Min(300, warmupMs + 1)), ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, homeUrl);
            ApplyNavigationHeaders(request, homeUrl, referer: null, acceptLanguage);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            _ = await response.Content.ReadAsStringAsync(ct);
        }

        private async Task<string> FetchHtmlAsync(
            HttpClient client,
            string pageUrl,
            string? referer,
            string acceptLanguage,
            CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            ApplyNavigationHeaders(request, pageUrl, referer, acceptLanguage);

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
            if (!LooksLikeAmazonProductHtml(body))
                throw new InvalidOperationException("Unexpected HTML (not a product page)");

            return body;
        }

        private static void ApplyNavigationHeaders(
            HttpRequestMessage request, string pageUrl, string? referer, string acceptLanguage)
        {
            AmazonBrowserProfile.ApplyNavigationHeaders(request, pageUrl, referer);
            request.Headers.Remove("Accept-Language");
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }

        private static bool LooksLikeBotChallenge(string html) =>
            html.Contains("api-services-support@amazon.com", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Enter the characters you see below", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Type the characters you see in this image", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Robot Check", StringComparison.OrdinalIgnoreCase)
            || html.Contains("/errors/validateCaptcha", StringComparison.OrdinalIgnoreCase)
            || html.Contains("opfcaptcha.amazon", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeAmazonProductHtml(string html) =>
            html.Contains("id=\"productTitle\"", StringComparison.OrdinalIgnoreCase)
            || html.Contains("id=\"dp\"", StringComparison.OrdinalIgnoreCase)
            || html.Contains("id=\"ppd\"", StringComparison.OrdinalIgnoreCase)
            || html.Contains("id=\"feature-bullets\"", StringComparison.OrdinalIgnoreCase)
            || html.Contains("data-asin=", StringComparison.OrdinalIgnoreCase);

        private sealed record WorkItem(int Id, string Asin, bool NeedAr, bool NeedEn);
    }
}
