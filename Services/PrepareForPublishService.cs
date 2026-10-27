using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Affiliate.Models;
using Microsoft.Playwright;

namespace Affiliate.Services
{
    public interface IPrepareForPublishService
    {
        /// <summary>
        /// Opens Amazon.eg (direct host first), ensures Arabic UI, and returns a publish screenshot.
        /// Products with an automatic checkout discount or Amazon promo-code coupon use a mobile
        /// cart screenshot with an injected discount row (Amazon hides that until login).
        /// Otherwise: highlighted DP when prices match; cart when they do not.
        /// If the DP price cannot be read, that attempt fails and the next route (proxy) is tried.
        /// On direct-host timeout/captcha/block/null price, retries via ISP SOCKS5 proxies.
        /// </summary>
        Task<PrepareForPublishResult> PrepareAsync(
            string asin,
            decimal? alertPrice,
            Guid? correlationId = null,
            CancellationToken cancellationToken = default);
    }

    public sealed record PrepareForPublishResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        /// <summary>Raw exception / Playwright detail for ops (sent to the publish group).</summary>
        public string? Detail { get; init; }
        /// <summary><c>direct</c> or proxy describe string used for the successful/final attempt.</summary>
        public string? Route { get; init; }
        public string? ProductName { get; init; }
        public string? ProductUrl { get; init; }
        public decimal? ProductPrice { get; init; }
        public byte[]? ScreenshotPng { get; init; }
        public bool UsedCartScreenshot { get; init; }
        /// <summary>Audit trail id shared with Telegram Requested/Preview/Confirm events.</summary>
        public Guid CorrelationId { get; init; }
        /// <summary>
        /// Arabic note(s) for Telegram captions (checkout discount and/or stale product-page price);
        /// null otherwise. Shown before the product URL; multiple notes are newline-separated.
        /// </summary>
        public string? CaptionNote { get; init; }
    }

    /// <summary>
    /// Discount detected on the DP to fake in the logged-out cart summary.
    /// <see cref="Kind"/> is <c>auto</c> (عند الدفع → "Extra N% OFF") or
    /// <c>promo</c> (رمز العرض → "Save N%").
    /// </summary>
    internal sealed record CheckoutDiscountInfo(string Kind, int Percent, string Label);

    public sealed class PrepareForPublishService : IPrepareForPublishService
    {
        private const int MaxProxyAttempts = 20;
        private const int ProxyParallelism = 5;
        private static readonly SemaphoreSlim BrowserGate = new(1, 1);
        private static readonly SemaphoreSlim SharedBrowserLock = new(1, 1);
        private static int _browsersPathConfigured;
        private static IPlaywright? _sharedPlaywright;
        private static IBrowser? _sharedBrowser;

        private readonly IIspProxyService _ispProxy;
        private readonly IPublishAuditLogger _audit;
        private readonly ILogger<PrepareForPublishService> _logger;

        public PrepareForPublishService(
            IIspProxyService ispProxy,
            IPublishAuditLogger audit,
            ILogger<PrepareForPublishService> logger)
        {
            _ispProxy = ispProxy;
            _audit = audit;
            _logger = logger;
        }

        public async Task<PrepareForPublishResult> PrepareAsync(
            string asin,
            decimal? alertPrice,
            Guid? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            asin = (asin ?? string.Empty).Trim().ToUpperInvariant();
            var corr = correlationId ?? Guid.NewGuid();
            if (asin.Length is < 8 or > 16 || asin.Any(c => !(char.IsLetterOrDigit(c))))
            {
                return Fail("ASIN غير صالح.", correlationId: corr);
            }

            EnsureBrowsersPath();

            var productUrl = BuildProductUrl(asin);
            await BrowserGate.WaitAsync(cancellationToken);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(3));

                var result = await CaptureWithFallbackAsync(asin, alertPrice, productUrl, corr, cts.Token);
                // Duration lives only on Attempt rows; full session time is computed in the UI.
                _audit.Log(corr, asin,
                    result.Success ? PublishAuditEvent.Finished : PublishAuditEvent.Failed,
                    route: result.Route,
                    pageKind: result.UsedCartScreenshot ? "Cart" : (result.Success ? "Dp" : null),
                    success: result.Success,
                    detail: result.Success ? null : TruncateAudit(result.Detail ?? result.Error));
                return result with { CorrelationId = corr };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _audit.Log(corr, asin, PublishAuditEvent.Failed,
                    success: false,
                    detail: "Overall prepare timeout (3 minutes)");
                return Fail(
                    "انتهت مهلة تجهيز المنتج للنشر.",
                    "Overall prepare timeout (3 minutes).",
                    correlationId: corr);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Prepare-for-publish failed for {Asin}", asin);
                _audit.Log(corr, asin, PublishAuditEvent.Failed,
                    success: false,
                    detail: TruncateAudit(FormatExceptionDetail(ex)));
                return Fail(DescribeFailure(ex), FormatExceptionDetail(ex), correlationId: corr);
            }
            finally
            {
                BrowserGate.Release();
            }
        }

        /// <summary>
        /// Prefer the user-local Playwright browsers folder so VS/IIS runs
        /// don't depend on a Cursor sandbox PLAYWRIGHT_BROWSERS_PATH.
        /// </summary>
        private static void EnsureBrowsersPath()
        {
            if (Interlocked.Exchange(ref _browsersPathConfigured, 1) == 1)
                return;

            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ms-playwright");

            // If a sandbox/temp path is set but does not contain a chromium folder matching
            // this package, force the stable user location.
            var current = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
            var useLocal = string.IsNullOrWhiteSpace(current)
                           || current.Contains("cursor-sandbox-cache", StringComparison.OrdinalIgnoreCase)
                           || !Directory.Exists(current);

            if (useLocal && Directory.Exists(local))
                Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", local);
        }

        private async Task<PrepareForPublishResult> CaptureWithFallbackAsync(
            string asin,
            decimal? alertPrice,
            string productUrl,
            Guid correlationId,
            CancellationToken cancellationToken)
        {
            var attemptLogs = new List<string>();
            PrepareForPublishResult? lastFailure = null;

            var direct = await TryCaptureOnceAsync(
                asin, alertPrice, productUrl, proxy: null, correlationId, cancellationToken);
            if (direct.Success)
                return WithRoute(direct, "direct", correlationId);

            lastFailure = WithRoute(direct, "direct", correlationId);
            attemptLogs.Add(FormatAttemptLog("direct", direct));

            if (!IsProxyFallbackCandidate(direct))
                return lastFailure;

            _logger.LogWarning(
                "Prepare-for-publish {Asin} failed on VPS/direct host ({Error}); falling back to ISP proxies",
                asin, direct.Error);

            var proxyProbe = _ispProxy.GetEndpoint();
            if (!proxyProbe.UseProxy)
            {
                return lastFailure ?? Fail(
                    "فشل تجهيز المنتج للنشر.",
                    "ISP proxies disabled or empty; no fallback route.",
                    correlationId: correlationId);
            }

            var triedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var proxiesTried = 0;

            while (proxiesTried < MaxProxyAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batchSize = Math.Min(ProxyParallelism, MaxProxyAttempts - proxiesTried);
                var batch = TakeDistinctProxies(batchSize, triedKeys);
                if (batch.Count == 0)
                    break;

                proxiesTried += batch.Count;
                _logger.LogInformation(
                    "Prepare-for-publish {Asin}: proxy wave of {Count} (tried {Tried}/{Max})",
                    asin, batch.Count, proxiesTried, MaxProxyAttempts);

                var (winner, waveFailures) = await RaceProxyBatchAsync(
                    asin, alertPrice, productUrl, batch, correlationId, cancellationToken);

                foreach (var failure in waveFailures)
                {
                    attemptLogs.Add(FormatAttemptLog(failure.Route ?? "proxy", failure));
                    lastFailure = failure;
                    if (IsFatalPrepareFailure(failure))
                    {
                        return Fail(
                            failure.Error ?? "فشل تجهيز المنتج للنشر.",
                            string.Join("\n", attemptLogs),
                            failure.Route,
                            correlationId);
                    }
                }

                if (winner is { Success: true })
                {
                    _logger.LogInformation(
                        "Prepare-for-publish {Asin} succeeded via proxy {Proxy}",
                        asin, winner.Route);
                    return winner;
                }
            }

            var detail = string.Join("\n", attemptLogs);
            if (lastFailure is null)
                return Fail("فشل تجهيز المنتج للنشر.", detail, correlationId: correlationId);

            return Fail(
                lastFailure.Error ?? "فشل تجهيز المنتج للنشر.",
                detail,
                lastFailure.Route,
                correlationId);
        }

        /// <summary>
        /// Runs up to <see cref="ProxyParallelism"/> captures together; cancels siblings when one wins.
        /// Does not ReportFailure/Success — publish must not block proxies used by ASIN recheck.
        /// </summary>
        private async Task<(PrepareForPublishResult? Winner, List<PrepareForPublishResult> Failures)> RaceProxyBatchAsync(
            string asin,
            decimal? alertPrice,
            string productUrl,
            IReadOnlyList<IspProxyEndpoint> batch,
            Guid correlationId,
            CancellationToken cancellationToken)
        {
            using var waveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var waveToken = waveCts.Token;
            var failures = new List<PrepareForPublishResult>();

            var tasks = batch.Select(async endpoint =>
            {
                var route = AuditRoute(endpoint);
                try
                {
                    if (!await ProbeProxyReachableAsync(endpoint, waveToken))
                    {
                        _audit.Log(correlationId, asin, PublishAuditEvent.Attempt,
                            route: route, proxyHost: endpoint.Host, proxyPort: endpoint.Port,
                            success: false, detail: "HTTP probe failed");
                        return Fail(
                            "فشل تجهيز المنتج للنشر.",
                            "HTTP probe failed",
                            route,
                            correlationId);
                    }

                    var result = await TryCaptureOnceAsync(
                        asin, alertPrice, productUrl, endpoint, correlationId, waveToken);

                    if (result.Success)
                    {
                        try { waveCts.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
                        return WithRoute(result, route, correlationId);
                    }

                    return WithRoute(result, route, correlationId);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Fail(
                        "ألغي بعد نجاح بروكسي آخر.",
                        "Cancelled after sibling proxy succeeded",
                        route,
                        correlationId);
                }
            }).ToArray();

            var results = await Task.WhenAll(tasks);
            PrepareForPublishResult? winner = null;
            foreach (var result in results)
            {
                if (result.Success && winner is null)
                    winner = result;
                else if (!result.Success
                         && !string.Equals(
                             result.Detail,
                             "Cancelled after sibling proxy succeeded",
                             StringComparison.Ordinal))
                    failures.Add(result);
            }

            return (winner, failures);
        }

        private List<IspProxyEndpoint> TakeDistinctProxies(int count, HashSet<string> alreadyTried)
        {
            var batch = new List<IspProxyEndpoint>(count);
            var spins = 0;
            var maxSpins = Math.Max(count * 15, 30);

            while (batch.Count < count && spins++ < maxSpins)
            {
                var endpoint = _ispProxy.GetEndpoint();
                if (!endpoint.UseProxy || endpoint.RouteThroughTranslate)
                    continue;
                if (!alreadyTried.Add(endpoint.Key))
                    continue;
                batch.Add(endpoint);
            }

            return batch;
        }

        private async Task<PrepareForPublishResult> TryCaptureOnceAsync(
            string asin,
            decimal? alertPrice,
            string productUrl,
            IspProxyEndpoint? proxy,
            Guid correlationId,
            CancellationToken cancellationToken)
        {
            var route = AuditRoute(proxy);
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await CaptureOnceAsync(
                    asin, alertPrice, productUrl, proxy, correlationId, cancellationToken);
                _audit.Log(correlationId, asin, PublishAuditEvent.Attempt,
                    route: route,
                    proxyHost: proxy?.Host,
                    proxyPort: proxy is { UseProxy: true } ? proxy.Port : null,
                    pageKind: result.UsedCartScreenshot ? "Cart" : (result.Success ? "Dp" : null),
                    success: result.Success,
                    durationMs: (int)sw.ElapsedMilliseconds,
                    detail: TruncateAudit(result.Detail ?? result.Error));
                // Detail on success is path info for audit only — clear so Telegram failure logs stay clean.
                return result with
                {
                    CorrelationId = correlationId,
                    Route = route,
                    Detail = result.Success ? null : result.Detail
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Prepare-for-publish attempt failed for {Asin} via {Route}", asin, route);
                _audit.Log(correlationId, asin, PublishAuditEvent.Attempt,
                    route: route,
                    proxyHost: proxy?.Host,
                    proxyPort: proxy is { UseProxy: true } ? proxy.Port : null,
                    success: false,
                    durationMs: (int)sw.ElapsedMilliseconds,
                    detail: TruncateAudit(FormatExceptionDetail(ex)));
                return Fail(DescribeFailure(ex), FormatExceptionDetail(ex), route, correlationId);
            }
        }

        private async Task<PrepareForPublishResult> CaptureOnceAsync(
            string asin,
            decimal? alertPrice,
            string productUrl,
            IspProxyEndpoint? proxy,
            Guid correlationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var route = AuditRoute(proxy);

            // Chromium cannot auth SOCKS5; bridge local no-auth SOCKS5 → upstream Webshare SOCKS5.
            LocalSocks5Bridge? socksBridge = null;
            Proxy? playwrightProxy = null;
            if (proxy is { UseProxy: true } && !string.IsNullOrWhiteSpace(proxy.Host))
            {
                socksBridge = LocalSocks5Bridge.Start(
                    proxy.Host!, proxy.Port, proxy.Username, proxy.Password);
                playwrightProxy = new Proxy { Server = socksBridge.PlaywrightServer };
            }

            await using (socksBridge)
            {
                var browser = await GetSharedBrowserAsync();
                await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = 1000, Height = 1100 },
                    DeviceScaleFactor = 2,
                    Locale = "ar-AE",
                    TimezoneId = "Africa/Cairo",
                    UserAgent =
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                    ExtraHTTPHeaders = new Dictionary<string, string>
                    {
                        ["Accept-Language"] = "ar-AE,ar;q=0.9,en-AE;q=0.5,en;q=0.4"
                    },
                    Proxy = playwrightProxy
                });

                await context.AddCookiesAsync(
                [
                    new Cookie
                    {
                        Name = "lc-acbeg",
                        Value = "ar_AE",
                        Domain = ".amazon.eg",
                        Path = "/"
                    },
                    new Cookie
                    {
                        Name = "i18n-prefs",
                        Value = "EGP",
                        Domain = ".amazon.eg",
                        Path = "/"
                    },
                    new Cookie
                    {
                        Name = "skin",
                        Value = "noskin",
                        Domain = ".amazon.eg",
                        Path = "/"
                    }
                ]);

                var page = await context.NewPageAsync();
                page.SetDefaultTimeout(45_000);

                await NavigateToArabicProductAsync(page, asin, productUrl, cancellationToken);

                if (await LooksBlockedAsync(page))
                {
                    return Fail(
                        "أمازون طلب تحقق (captcha) — أعد المحاولة لاحقاً.",
                        $"Captcha/robot check via {route}",
                        route,
                        correlationId);
                }

                var productName = await ReadProductTitleAsync(page) ?? asin;
                var pagePrice = await ReadDesktopPriceAsync(page);

                _logger.LogInformation(
                    "Prepare-for-publish {Asin} via {Route}: pagePrice={PagePrice}, alertPrice={AlertPrice}, lang={Lang}",
                    asin, route, pagePrice, alertPrice, await ReadHtmlLangAsync(page));

                // No DP price → fail this route so CaptureWithFallbackAsync tries proxies
                // (do not force cart for MAP / unread price).
                if (pagePrice is null)
                {
                    return Fail(
                        "ما قدرناش نقرأ سعر صفحة المنتج — هنحاول مسار تاني.",
                        "pagePrice=null; retry via proxy",
                        route,
                        correlationId);
                }

                var pricesMatch = alertPrice is null
                    || PricesMatch(pagePrice.Value, alertPrice.Value);

                var checkoutDiscount = await DetectCheckoutDiscountAsync(page);
                if (checkoutDiscount is not null)
                {
                    _logger.LogInformation(
                        "Prepare-for-publish {Asin}: checkout discount {Kind} {Percent}% → cart with injected summary",
                        asin, checkoutDiscount.Kind, checkoutDiscount.Percent);
                }

                // Auto/promo discounts: always cart + drawn summary (not shown until login).
                // Price mismatch vs DB: cart. Null page price is handled above (proxy retry).
                var cartReasonAr = DescribeCartReasonAr(checkoutDiscount, pagePrice, alertPrice, pricesMatch);
                var useCart = cartReasonAr is not null;

                byte[] screenshot;
                var usedCart = false;
                decimal? resultPrice = pagePrice ?? alertPrice;
                // Always store the path decision in Detail so publish logs show WHY cart/DP.
                string? pathDetail = useCart
                    ? $"سبب السلة: {cartReasonAr}"
                    : alertPrice is null
                        ? "سبب المسار: مفيش سعر محفوظ → صفحة المنتج"
                        : "سبب المسار: الأسعار متطابقة → صفحة المنتج";

                if (!useCart)
                {
                    await UseNarrowPublishLayoutAsync(page);
                    await HighlightDesktopPublishTargetsAsync(page);
                    screenshot = await CapturePublishScreenshotAsync(page, cart: false);
                }
                else
                {
                    try
                    {
                        await AddToCartAndOpenCartAsync(page, asin, cancellationToken);
                        usedCart = true;
                        screenshot = await CaptureMobileCartItemAsync(
                            browser, context, asin, playwrightProxy, checkoutDiscount, pagePrice);
                        if (checkoutDiscount is not null && pagePrice is { } listPrice)
                            resultPrice = ApplyCheckoutDiscount(listPrice, checkoutDiscount.Percent);
                        else
                            resultPrice = alertPrice ?? pagePrice;
                    }
                    catch (Exception cartEx)
                    {
                        // Cart often fails (interstitial / soft block). Fall back to highlighted DP.
                        _logger.LogWarning(cartEx,
                            "Prepare-for-publish cart path failed for {Asin} via {Route} (reason={Reason}); falling back to DP screenshot",
                            asin, route, cartReasonAr);

                        var failShort = TruncateAudit(cartEx.Message) ?? "timeout/error";
                        _audit.Log(correlationId, asin, PublishAuditEvent.CartFallback,
                            route: route,
                            proxyHost: proxy?.Host,
                            proxyPort: proxy is { UseProxy: true } ? proxy.Port : null,
                            pageKind: "Dp",
                            success: true,
                            detail: TruncateAudit(
                                $"سبب اختيار السلة: {cartReasonAr} | فشل التنفيذ: {failShort}"));

                        await page.GotoAsync(productUrl, new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 45_000
                        });
                        await WaitForVisibleProductSignalAsync(page);

                        await UseNarrowPublishLayoutAsync(page);
                        await HighlightDesktopPublishTargetsAsync(page);
                        screenshot = await CapturePublishScreenshotAsync(page, cart: false);
                        usedCart = false;
                        resultPrice = pagePrice ?? alertPrice;
                        pathDetail =
                            $"سبب السلة: {cartReasonAr} | السلة فشلت فرجعنا لصفحة المنتج | {failShort}";
                    }
                }

                return new PrepareForPublishResult
                {
                    Success = true,
                    Route = route,
                    CorrelationId = correlationId,
                    ProductName = productName,
                    ProductUrl = productUrl,
                    ProductPrice = resultPrice,
                    ScreenshotPng = screenshot,
                    UsedCartScreenshot = usedCart,
                    Detail = pathDetail,
                    CaptionNote = FormatPublishCaptionNotes(checkoutDiscount, priceMismatch: !pricesMatch)
                };
            } // socksBridge
        }

        /// <summary>
        /// One warm Chromium for all prepare attempts; each capture still gets a fresh context.
        /// </summary>
        private static async Task<IBrowser> GetSharedBrowserAsync()
        {
            if (_sharedBrowser is { IsConnected: true })
                return _sharedBrowser;

            await SharedBrowserLock.WaitAsync();
            try
            {
                if (_sharedBrowser is { IsConnected: true })
                    return _sharedBrowser;

                if (_sharedBrowser is not null)
                {
                    try { await _sharedBrowser.DisposeAsync(); }
                    catch { /* already dead */ }
                    _sharedBrowser = null;
                }

                _sharedPlaywright ??= await Playwright.CreateAsync();
                try
                {
                    _sharedBrowser = await _sharedPlaywright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = true,
                        Args =
                        [
                            "--disable-blink-features=AutomationControlled",
                            "--no-sandbox",
                            "--disable-dev-shm-usage"
                        ]
                    });
                }
                catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Chromium غير مثبت لـ Playwright. من مجلد المشروع نفّذ: " +
                        "powershell -ExecutionPolicy Bypass -File bin\\Debug\\net8.0\\playwright.ps1 install chromium",
                        ex);
                }

                return _sharedBrowser;
            }
            finally
            {
                SharedBrowserLock.Release();
            }
        }

        /// <summary>
        /// Cheap HTTP check before launching Chromium — skips dead/blocked proxies quickly.
        /// Uses the same SOCKS5 client stack as the scraper.
        /// </summary>
        private async Task<bool> ProbeProxyReachableAsync(
            IspProxyEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            try
            {
                using var client = _ispProxy.CreateClient(endpoint);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await client.GetAsync(
                    "https://www.amazon.eg/robots.txt", cts.Token);
                var ok = (int)response.StatusCode is >= 200 and < 500;
                _logger.LogDebug(
                    "Prepare-for-publish proxy probe {Proxy} => {Status}",
                    endpoint.Describe(), (int)response.StatusCode);
                return ok;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Prepare-for-publish proxy probe failed for {Proxy}", endpoint.Describe());
                return false;
            }
        }

        private static bool IsProxyFallbackCandidate(PrepareForPublishResult result)
        {
            if (result.Success || IsFatalPrepareFailure(result))
                return false;

            var error = result.Error ?? "";
            var detail = result.Detail ?? "";
            return error.Contains("مهلة", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("captcha", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("تحقق", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("سعر صفحة المنتج", StringComparison.OrdinalIgnoreCase)
                   || detail.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                   || detail.Contains("Captcha", StringComparison.OrdinalIgnoreCase)
                   || detail.Contains("pagePrice=null", StringComparison.OrdinalIgnoreCase)
                   || detail.Contains("net::", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("فشل تجهيز", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFatalPrepareFailure(PrepareForPublishResult result)
        {
            var error = result.Error ?? "";
            var detail = result.Detail ?? "";
            return error.Contains("Chromium غير مثبت", StringComparison.OrdinalIgnoreCase)
                   || detail.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatAttemptLog(string route, PrepareForPublishResult result)
        {
            var error = result.Error ?? "failed";
            return string.IsNullOrWhiteSpace(result.Detail)
                ? $"{route}: {error}"
                : $"{route}: {error} | {result.Detail}";
        }

        private static string AuditRoute(IspProxyEndpoint? proxy) =>
            proxy is { UseProxy: true } && !string.IsNullOrWhiteSpace(proxy.Host)
                ? $"{proxy.Host}:{proxy.Port}"
                : "direct";

        private static string? TruncateAudit(string? text, int max = 500)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            text = text.Trim();
            return text.Length <= max ? text : text[..max];
        }

        private static PrepareForPublishResult WithRoute(
            PrepareForPublishResult result,
            string route,
            Guid correlationId) =>
            result with { Route = route, CorrelationId = correlationId };

        /// <summary>Telegram caption lines placed before the URL (discount and/or cart price hint).</summary>
        internal static string? FormatPublishCaptionNotes(
            CheckoutDiscountInfo? discount,
            bool priceMismatch)
        {
            var lines = new List<string>(2);

            if (discount is { Percent: > 0 })
            {
                lines.Add(discount.Kind.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? $"ملحوظة: حط السعر في السلة وهتلاقي خصم تلقائي عند الدفع ({discount.Percent}%)"
                    : "ملحوظة: يوجد كود خصم للتحصيل عند الشراء هتلاقية تحت المنتج");
            }

            if (priceMismatch)
            {
                lines.Add("ملحوظة: السعر ممكن يكون لسه متحدثش في صفحة المنتج حط في السلة هيتحدث");
            }

            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        /// <summary>
        /// language=ar_AE alone is unreliable — also set cookies/headers/locale,
        /// then fall back to the /-/ar/ path when html[lang] is not Arabic.
        /// </summary>
        private static async Task NavigateToArabicProductAsync(
            IPage page,
            string asin,
            string productUrl,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await page.GotoAsync(productUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45_000
            });

            await WaitForVisibleProductSignalAsync(page);

            if (await IsArabicUiAsync(page))
                return;

            var arPathUrl = $"https://www.amazon.eg/-/ar/dp/{asin}?language=ar_AE";
            await page.GotoAsync(arPathUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45_000
            });
            await WaitForVisibleProductSignalAsync(page);
        }

        /// <summary>
        /// Amazon often has multiple matching nodes (e.g. hidden duplicate #corePrice_feature_div).
        /// WaitForSelector on a comma list waits for the first DOM match to be visible — which can
        /// stay hidden forever while a later match is already visible. Prefer any visible signal.
        /// </summary>
        private static async Task WaitForVisibleProductSignalAsync(IPage page, float timeoutMs = 30_000)
        {
            await page.Locator("#productTitle, #add-to-cart-button, #corePrice_feature_div")
                .Locator("visible=true")
                .First
                .WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = timeoutMs
                });
        }

        private static async Task<bool> LooksBlockedAsync(IPage page)
        {
            var content = await page.ContentAsync();
            return content.Contains("Enter the characters you see", StringComparison.OrdinalIgnoreCase)
                   || content.Contains("Type the characters you see", StringComparison.OrdinalIgnoreCase)
                   || content.Contains("api/validateCaptcha", StringComparison.OrdinalIgnoreCase)
                   || content.Contains("Robot Check", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<bool> IsArabicUiAsync(IPage page)
        {
            var lang = await ReadHtmlLangAsync(page);
            if (!string.IsNullOrWhiteSpace(lang) &&
                lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var marker = page.Locator("text=إضافة إلى عربة التسوق");
            return await marker.CountAsync() > 0;
        }

        private static async Task<string?> ReadHtmlLangAsync(IPage page)
        {
            try
            {
                return await page.EvalOnSelectorAsync<string?>("html", "el => el.getAttribute('lang')");
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string?> ReadProductTitleAsync(IPage page)
        {
            try
            {
                var title = page.Locator("#productTitle");
                if (await title.CountAsync() == 0)
                    return null;
                var text = (await title.First.InnerTextAsync())?.Trim();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<decimal?> ReadDesktopPriceAsync(IPage page)
        {
            // Locator GetAttribute — no Evaluate/WaitFor. Retry while apex hydrates.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    var attrs = page.Locator("[data-csa-c-price-to-pay]");
                    var n = await attrs.CountAsync();
                    for (var i = 0; i < n; i++)
                    {
                        var parsed = ParsePrice(await attrs.Nth(i).GetAttributeAsync("data-csa-c-price-to-pay"));
                        if (parsed is > 0)
                            return parsed;
                    }

                    var a11y = page.Locator("#apex-pricetopay-accessibility-label");
                    if (await a11y.CountAsync() > 0)
                    {
                        var parsed = ParsePrice(await a11y.First.TextContentAsync(
                            new LocatorTextContentOptions { Timeout = 2_000 }));
                        if (parsed is > 0)
                            return parsed;
                    }

                    var whole = page.Locator(".a-price.priceToPay .a-price-whole");
                    if (await whole.CountAsync() > 0)
                    {
                        // Whole text is "429.\u200E" (nested decimal + LRM); keep digits/commas only.
                        var wholeText = Regex.Replace(await whole.First.TextContentAsync(
                            new LocatorTextContentOptions { Timeout = 2_000 }) ?? "", @"[^\d,]", "");
                        var fracLoc = page.Locator(".a-price.priceToPay .a-price-fraction");
                        var frac = await fracLoc.CountAsync() > 0
                            ? (await fracLoc.First.TextContentAsync(
                                new LocatorTextContentOptions { Timeout = 1_000 }) ?? "00")
                            : "00";
                        var parsed = ParsePrice($"{wholeText}.{frac}");
                        if (parsed is > 0)
                            return parsed;
                    }
                }
                catch (PlaywrightException)
                {
                    // retry
                }

                await Task.Delay(500);
            }

            return null;
        }

        /// <summary>
        /// Hides Amazon chrome (nav / promo bars) and captures from the product title
        /// (or cart content) downward — no header in the publish image.
        /// </summary>
        private static async Task<byte[]> CapturePublishScreenshotAsync(IPage page, bool cart)
        {
            await page.EvaluateAsync(
                """
                () => {
                  const hide = (sel) => {
                    document.querySelectorAll(sel).forEach(el => {
                      el.style.setProperty('display', 'none', 'important');
                      el.style.setProperty('visibility', 'hidden', 'important');
                      el.style.setProperty('height', '0', 'important');
                      el.style.setProperty('max-height', '0', 'important');
                      el.style.setProperty('overflow', 'hidden', 'important');
                      el.style.setProperty('pointer-events', 'none', 'important');
                    });
                  };
                  [
                    '#navbar', '#navbar-main', '#nav-belt', '#nav-main', '#nav-subnav',
                    '#nav-progressive-subnav', '#nav-top', '#nav-logo-sprites',
                    '#nav-flyout-anchor', '#nav-progressive-search', '#nav-search',
                    '#nav-global-location-slot', '#nav-tools', '#nav-cart',
                    '#nav-hamburger-menu', '#nav-upnav', '#nav-swmslot',
                    '#skyline-nav-wrapper', '#nav-main-copy',
                    'header[data-cel-widget]', '#rhf', '#navFooter',
                    '#gw-card-layout', '#nav-progressive-toc',
                    '#nav-breadcrumb-copy', '#wayfinding-breadcrumbs_container',
                    '#wayfinding-breadcrumbs_feature_div', '#navBackToTop'
                  ].forEach(hide);

                  // Promo / localization strips that sit under the main nav.
                  document.querySelectorAll(
                    '#nav-main, .nav-sprite-v3, [id^="nav-"], #navbarBackToTop'
                  ).forEach(el => {
                    if (el.closest('#dp') || el.closest('#ppd') || el.closest('#dp-container') ||
                        el.closest('#sc-active-cart') || el.closest('#activeCartViewForm'))
                      return;
                    if (el.id === 'navbar' || el.id?.startsWith('nav-') ||
                        el.className?.toString().includes('nav-')) {
                      el.style.setProperty('display', 'none', 'important');
                    }
                  });
                }
                """);

            // Let layout settle after hiding chrome.
            await page.WaitForTimeoutAsync(250);

            ILocator start = cart
                ? page.Locator("#sc-active-cart, #activeCartViewForm, #sc-retail-cart-container").First
                : page.Locator("#title, #titleSection, #title_feature_div, #productTitle").First;

            try
            {
                await start.ScrollIntoViewIfNeededAsync();
            }
            catch (PlaywrightException)
            {
                // fall through
            }

            var box = await start.BoundingBoxAsync();
            var viewport = page.ViewportSize;
            if (box is null || viewport is null || box.Width < 50)
            {
                return await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Type = ScreenshotType.Png,
                    FullPage = false
                });
            }

            var y = Math.Max(0f, box.Y - 4f);

            // Crop tightly: title → buybox / seller / center price (no long product details).
            var contentBottom = await page.EvaluateAsync<float>(
                """
                (isCart) => {
                  const bottoms = [];
                  const add = (el) => {
                    if (!el) return;
                    const r = el.getBoundingClientRect();
                    if (r.height > 0) bottoms.push(r.bottom);
                  };
                  if (isCart) {
                    add(document.querySelector('#sc-active-cart .sc-list-item'));
                    add(document.querySelector('#sc-active-cart'));
                    add(document.querySelector('#activeCartViewForm'));
                  } else {
                    add(document.querySelector('#desktop_buybox'));
                    add(document.querySelector('#buybox'));
                    add(document.querySelector('#offerDisplayFeatures_desktop'));
                    add(document.querySelector('#merchantInfoFeature_feature_div'));
                    add(document.querySelector('#corePriceDisplay_desktop_feature_div'));
                    add(document.querySelector('#corePrice_feature_div'));
                    add(document.querySelector('#imgTagWrapperId'));
                    add(document.querySelector('#landingImage'));
                    // Auto checkout + Amazon promo code coupons only — not bank Visa/NBE/ABC.
                    const seenPromo = new Set();
                    const promoRow = (el) => {
                      let node = el;
                      while (node && node !== document.body) {
                        const style = (node.getAttribute && node.getAttribute('style')) || '';
                        if (/padding\s*:\s*5px/i.test(style)) return node;
                        if (node.getAttribute?.('data-csa-c-owner') === 'PromotionsDiscovery')
                          return node;
                        node = node.parentElement;
                      }
                      return el;
                    };
                    const isCheckoutPromoRow = (row) => {
                      if (!row) return false;
                      const text = (row.innerText || '').replace(/\s+/g, ' ');
                      const hasPspLink = !!row.querySelector(
                        'a.cxcwEmphasisLink[href*="/promotion/psp/"], a[href*="/promotion/psp/"], a#emphasisLink, #emphasisLink');
                      if (text.includes('عند الدفع')) return true;
                      if (text.includes('رمز العرض الترويجي')) return true;
                      if (row.querySelector('.a-alert-inline-success') && hasPspLink) return true;
                      if (row.querySelector('[id^="promoMessageCXCW"]') && hasPspLink) return true;
                      return false;
                    };
                    document.querySelectorAll(
                      '[id^="promoMessageCXCW"], [id^="doneMessage"], [id^="greenBadge"], [id^="clickMe"], [data-csa-c-action="clipPromotion"], .promoPriceBlockMessage [data-csa-c-owner="PromotionsDiscovery"], [data-csa-c-owner="PromotionsDiscovery"], .a-alert-inline-success')
                      .forEach((el) => {
                        const target = promoRow(el);
                        if (!target || seenPromo.has(target) || !isCheckoutPromoRow(target)) return;
                        seenPromo.add(target);
                        add(target);
                      });
                  }
                  return bottoms.length ? Math.max(...bottoms) : window.innerHeight;
                }
                """,
                cart);

            const float maxHeight = 1500f;
            const float pad = 60f;
            var height = Math.Clamp(contentBottom - y + pad, 400f, maxHeight);
            height = Math.Min(height, viewport.Height - y);

            return await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Png,
                FullPage = false,
                Clip = new Clip
                {
                    X = 0,
                    Y = y,
                    Width = viewport.Width,
                    Height = height
                }
            });
        }

        /// <summary>
        /// Reopens the cart as a phone (same session/cookies) and captures the product card.
        /// When <paramref name="discount"/> is set, injects a fake order-summary discount row
        /// (Amazon only shows it after login) and captures item + summary together.
        /// </summary>
        private static async Task<byte[]> CaptureMobileCartItemAsync(
            IBrowser browser,
            IBrowserContext desktopContext,
            string asin,
            Proxy? proxy,
            CheckoutDiscountInfo? discount,
            decimal? itemPrice)
        {
            await using var mobile = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                StorageState = await desktopContext.StorageStateAsync(),
                ViewportSize = new ViewportSize { Width = 412, Height = 915 },
                DeviceScaleFactor = 2.5f,
                IsMobile = true,
                HasTouch = true,
                Locale = "ar-AE",
                TimezoneId = "Africa/Cairo",
                UserAgent =
                    "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36",
                ExtraHTTPHeaders = new Dictionary<string, string>
                {
                    ["Accept-Language"] = "ar-AE,ar;q=0.9,en-AE;q=0.5,en;q=0.4"
                },
                Proxy = proxy
            });

            var page = await mobile.NewPageAsync();
            await page.GotoAsync(
                "https://www.amazon.eg/-/ar/gp/cart/view.html?language=ar_AE",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45_000 });

            var item = page.Locator($".sc-list-item[data-asin='{asin}'], [data-asin='{asin}'][data-itemtype='active']").First;
            await item.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

            if (discount is null || itemPrice is null || discount.Percent <= 0)
                return await item.ScreenshotAsync(new LocatorScreenshotOptions { Type = ScreenshotType.Png });

            await InjectCartCheckoutDiscountAsync(page, asin, itemPrice.Value, discount);

            await page.EvaluateAsync(
                """
                (asin) => {
                  const hide = (el) => {
                    if (!el) return;
                    el.style.setProperty('display', 'none', 'important');
                    el.style.setProperty('visibility', 'hidden', 'important');
                  };
                  for (const sel of [
                    '#sc-buy-box', '#sc-proceed-to-checkout', '#sc-buy-box-ptc',
                    '[name="proceedToRetailCheckout"]', '#sc-active-cart .sc-proceed-to-checkout',
                    '#nav-main', '#navbar', '#nav-belt', '#nav-subnav',
                    '#nav-progressive-subnav', 'header', '#huc-v2-order-row-with-divider'
                  ]) {
                    document.querySelectorAll(sel).forEach(hide);
                  }
                  document.querySelectorAll('input, button, a, span, .a-button').forEach((el) => {
                    const t = ((el.value || el.innerText || el.textContent || '') + '').replace(/\s+/g, ' ');
                    if (!t.includes('تابع لإتمام') && !t.includes('إتمام عملية الشراء') &&
                        !t.includes('Proceed to checkout'))
                      return;
                    hide(el.closest('.a-button, form, .sc-buy-box, [data-feature-id]') || el);
                  });
                  const item = document.querySelector(
                    `.sc-list-item[data-asin='${asin}'], [data-asin='${asin}'][data-itemtype='active']`);
                  item?.scrollIntoView({ block: 'start', inline: 'nearest' });
                  window.scrollBy(0, -24);
                }
                """,
                asin);
            await page.WaitForTimeoutAsync(250);

            var clip = await page.EvaluateAsync<CartCaptureClip?>(
                """
                (asin) => {
                  const item = document.querySelector(
                    `.sc-list-item[data-asin='${asin}'], [data-asin='${asin}'][data-itemtype='active']`);
                  const summary = document.getElementById('affiliate-injected-cart-summary');
                  if (!item || !summary) return null;
                  const a = item.getBoundingClientRect();
                  const b = summary.getBoundingClientRect();
                  const top = Math.min(a.top, b.top);
                  const bottom = Math.max(a.bottom, b.bottom);
                  const pad = 24;
                  const y = Math.max(0, top - pad);
                  const height = Math.min(window.innerHeight - y, bottom - top + pad * 2);
                  if (height < 50) return null;
                  return { x: 0, y, width: window.innerWidth, height };
                }
                """,
                asin);

            if (clip is null)
                return await item.ScreenshotAsync(new LocatorScreenshotOptions { Type = ScreenshotType.Png });

            return await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Png,
                FullPage = false,
                Clip = new Clip
                {
                    X = clip.X,
                    Y = clip.Y,
                    Width = clip.Width,
                    Height = clip.Height
                }
            });
        }

        private sealed class CartCaptureClip
        {
            public float X { get; set; }
            public float Y { get; set; }
            public float Width { get; set; }
            public float Height { get; set; }
        }

        internal static decimal ApplyCheckoutDiscount(decimal listPrice, int percent)
        {
            var discount = Math.Round(listPrice * percent / 100m, 2, MidpointRounding.AwayFromZero);
            return Math.Max(0, listPrice - discount);
        }

        private static string FormatCartMoney(decimal amount) =>
            amount.ToString("0.00", CultureInfo.InvariantCulture) + " جنيه";

        /// <summary>
        /// Reads automatic checkout / Amazon promo-code coupons from the DP (not bank cards).
        /// </summary>
        private static async Task<CheckoutDiscountInfo?> DetectCheckoutDiscountAsync(IPage page)
        {
            var dto = await page.EvaluateAsync<CheckoutDiscountDto?>(
                """
                () => {
                  const promoRow = (el) => {
                    let node = el;
                    while (node && node !== document.body) {
                      const style = (node.getAttribute && node.getAttribute('style')) || '';
                      if (/padding\s*:\s*5px/i.test(style)) return node;
                      if (node.getAttribute?.('data-csa-c-owner') === 'PromotionsDiscovery')
                        return node;
                      node = node.parentElement;
                    }
                    return el;
                  };
                  const parsePercent = (text) => {
                    const m = (text || '').match(/وف[ِّ]?ر\s*(\d+)\s*%/i)
                      || (text || '').match(/Extra\s*(\d+)\s*%/i)
                      || (text || '').match(/(\d+)\s*%/);
                    return m ? parseInt(m[1], 10) : 0;
                  };
                  const seen = new Set();
                  const rows = [];
                  document.querySelectorAll(
                    '[id^="promoMessageCXCW"], [id^="doneMessage"], [id^="greenBadge"], [id^="clickMe"], [data-csa-c-action="clipPromotion"], .promoPriceBlockMessage [data-csa-c-owner="PromotionsDiscovery"], [data-csa-c-owner="PromotionsDiscovery"], .a-alert-inline-success')
                    .forEach((el) => {
                      const row = promoRow(el);
                      if (!row || seen.has(row)) return;
                      seen.add(row);
                      rows.push(row);
                    });

                  // Bank / ValU / card promos also say "at checkout" / "عند الدفع" and must NOT
                  // force the cart screenshot path (those are payment-method offers, not Amazon
                  // auto checkout or Amazon promo-code discounts).
                  const isBankOrPaymentPromo = (row, text) => {
                    if (row.querySelector('[data-csa-c-action="clipPromotion"], [id^="clickMe"]'))
                      return true;
                    return /Enter code|Credit Card|Visa|Mastercard|Vodafone|Valu|Buy Now,\s*Pay Later|بطاقة|فودافون|بنك|كود\s|Redeem|NBE|ABC\s*x?\s*Visa/i
                      .test(text);
                  };

                  let best = null;
                  for (const row of rows) {
                    const text = (row.innerText || '').replace(/\s+/g, ' ');
                    if (isBankOrPaymentPromo(row, text)) continue;

                    const hasPspLink = !!row.querySelector(
                      'a.cxcwEmphasisLink[href*="/promotion/psp/"], a[href*="/promotion/psp/"], a#emphasisLink, #emphasisLink');
                    const isAuto = text.includes('عند الدفع');
                    const isPromo =
                      text.includes('رمز العرض الترويجي') ||
                      (!!row.querySelector('[id^="promoMessageCXCW"]') && hasPspLink) ||
                      (!!row.querySelector('.a-alert-inline-success') && hasPspLink && !isAuto);
                    if (!isAuto && !isPromo) continue;

                    const badge = row.querySelector('[id^="greenBadge"]');
                    const percent = parsePercent(badge?.innerText || '') || parsePercent(text);
                    if (percent <= 0 || percent > 90) continue;

                    const kind = isAuto ? 'auto' : 'promo';
                    const label = kind === 'auto'
                      ? `Extra ${percent}% OFF`
                      : `Save ${percent}%`;
                    // Prefer auto over promo if both somehow match; otherwise first hit.
                    if (best && best.kind === 'auto' && kind === 'promo') continue;
                    best = { kind, percent, label };
                    if (kind === 'auto') break;
                  }
                  return best;
                }
                """);

            if (dto is null || dto.Percent <= 0 || string.IsNullOrWhiteSpace(dto.Kind))
                return null;

            var kind = dto.Kind.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "auto" : "promo";
            var label = string.IsNullOrWhiteSpace(dto.Label)
                ? (kind == "auto" ? $"Extra {dto.Percent}% OFF" : $"Save {dto.Percent}%")
                : dto.Label.Trim();

            return new CheckoutDiscountInfo(kind, dto.Percent, label);
        }

        private sealed class CheckoutDiscountDto
        {
            public string? Kind { get; set; }
            public int Percent { get; set; }
            public string? Label { get; set; }
        }

        /// <summary>
        /// Draws an Amazon-like cart summary with the discount line that only appears after login.
        /// </summary>
        private static async Task InjectCartCheckoutDiscountAsync(
            IPage page,
            string asin,
            decimal itemPrice,
            CheckoutDiscountInfo discount)
        {
            var discountAmount = Math.Round(
                itemPrice * discount.Percent / 100m, 2, MidpointRounding.AwayFromZero);
            var orderTotal = Math.Max(0, itemPrice - discountAmount);

            await page.EvaluateAsync(
                """
                ({ asin, itemPriceText, shipText, totalText, discountLabel, discountText, orderTotalText }) => {
                  document.getElementById('affiliate-injected-cart-summary')?.remove();

                  const item = document.querySelector(
                    `.sc-list-item[data-asin='${asin}'], [data-asin='${asin}'][data-itemtype='active']`);
                  if (!item) return;

                  const row = (label, value, opts = {}) => {
                    const div = document.createElement('div');
                    div.style.cssText = [
                      'display:flex',
                      'justify-content:space-between',
                      'align-items:baseline',
                      'gap:12px',
                      'padding:3px 0',
                      'font-size:14px',
                      'line-height:1.35',
                      opts.bold ? 'font-weight:700' : 'font-weight:400',
                      opts.green ? 'color:#067D62' : 'color:#0F1111'
                    ].join(';');
                    const lab = document.createElement('span');
                    lab.textContent = label;
                    lab.style.cssText = opts.green ? 'color:#0F1111' : '';
                    const val = document.createElement('span');
                    val.textContent = value;
                    if (opts.green) val.style.color = '#067D62';
                    if (opts.bold) val.style.fontWeight = '700';
                    div.appendChild(lab);
                    div.appendChild(val);
                    return div;
                  };

                  const box = document.createElement('div');
                  box.id = 'affiliate-injected-cart-summary';
                  box.dir = 'rtl';
                  box.style.cssText = [
                    'margin:0',
                    'padding:12px 14px 14px',
                    'background:#fff',
                    'border-top:1px solid #D5D9D9',
                    'font-family:Arial,"Amazon Ember",sans-serif',
                    'color:#0F1111'
                  ].join(';');

                  box.appendChild(row('السلع', itemPriceText));
                  box.appendChild(row('الشحن والتسليم', shipText));
                  box.appendChild(row('الإجمالي', totalText));
                  box.appendChild(row(discountLabel, discountText, { green: true }));
                  box.appendChild(row('إجمالي الطلب', orderTotalText, { bold: true }));

                  // Place after the cart item so the publish clip includes product + summary.
                  item.after(box);
                }
                """,
                new
                {
                    asin,
                    itemPriceText = FormatCartMoney(itemPrice),
                    shipText = FormatCartMoney(0m),
                    totalText = FormatCartMoney(itemPrice),
                    discountLabel = discount.Label,
                    discountText = "-" + FormatCartMoney(discountAmount),
                    orderTotalText = FormatCartMoney(orderTotal)
                });
        }

        /// <summary>
        /// Switches to a narrow, tall viewport so the publish image is portrait — Telegram
        /// scales images to the chat width, so a narrower capture shows much larger text.
        /// </summary>
        private static async Task UseNarrowPublishLayoutAsync(IPage page)
        {
            await page.SetViewportSizeAsync(PublishViewportWidth, PublishViewportHeight);
            await page.EvaluateAsync(
                """
                () => {
                  const style = document.createElement('style');
                  style.textContent = `
                    html, body, #a-page, #dp, #dp-container, #ppd, #centerCol, #rightCol,
                    #leftCol, #navbar, #nav-belt, #nav-main {
                      min-width: 0 !important;
                    }
                  `;
                  document.head.appendChild(style);

                  // Amazon's fixed column widths may still overflow; shrink the page to fit
                  // so the RTL buybox (left edge) is never clipped.
                  const overflow = document.documentElement.scrollWidth;
                  if (overflow > window.innerWidth + 4) {
                    document.body.style.zoom = (window.innerWidth / overflow).toString();
                  }
                  window.scrollTo(0, 0);
                }
                """);
            await page.WaitForTimeoutAsync(300);
        }

        private const int PublishViewportWidth = 720;
        private const int PublishViewportHeight = 1600;

        private static async Task HighlightDesktopPublishTargetsAsync(IPage page)
        {
            await page.EvaluateAsync(
                """
                () => {
                  const highlightColor = '#2E75B6';
                  const checkoutDiscountColor = '#C00000';
                  const outline = (el, color = highlightColor) => {
                    if (!el) return false;
                    el.style.outline = `2px solid ${color}`;
                    el.style.outlineOffset = '2px';
                    el.style.boxShadow = 'none';
                    return true;
                  };

                  // Only the center column price (below title / rating).
                  outline(
                    document.querySelector('#corePriceDisplay_desktop_feature_div') ||
                    document.querySelector('#centerCol #corePrice_feature_div') ||
                    document.querySelector('#centerCol .a-price.priceToPay')?.closest('[id], .a-section'));

                  // Exact buybox block: الشاحن / البائع (+ الدفع) — never fall back to huge buybox.
                  const hasShipper = (el) => {
                    const t = (el?.innerText || '').replace(/\s+/g, ' ');
                    return t.includes('الشاحن') || t.includes('البائع');
                  };

                  let seller =
                    document.querySelector('#offerDisplayFeatures_desktop') ||
                    document.querySelector('#merchantInfoFeature_feature_div');

                  if (!hasShipper(seller)) {
                    seller = null;
                    const labels = document.querySelectorAll(
                      '[offer-display-feature-name="desktop-merchant-info"], #merchantInfoFeature_feature_div, .offer-display-feature-label');
                    for (const label of labels) {
                      if (!hasShipper(label) && !(label.innerText || '').includes('الشاحن'))
                        continue;
                      seller =
                        label.closest('#offerDisplayFeatures_desktop') ||
                        label.closest('#offer-display-features') ||
                        label.closest('.offer-display-features-container') ||
                        document.querySelector('#merchantInfoFeature_feature_div') ||
                        label;
                      break;
                    }
                  }

                  // If payment row exists next to merchant, prefer the shared container.
                  if (seller && seller.id === 'merchantInfoFeature_feature_div') {
                    const container =
                      seller.closest('.offer-display-features-container') ||
                      seller.closest('#offer-display-features') ||
                      seller.closest('#offerDisplayFeatures_desktop');
                    if (container && hasShipper(container))
                      seller = container;
                  }

                  outline(seller);

                  // Auto checkout (عند الدفع) + Amazon promo code coupons (رمز العرض الترويجي / GLAM10).
                  // Do NOT match bank Visa/NBE/ABC coupons.
                  let checkoutDiscount = null;
                  const seenPromo = new Set();
                  const promoRow = (el) => {
                    let node = el;
                    while (node && node !== document.body) {
                      const style = (node.getAttribute && node.getAttribute('style')) || '';
                      // Amazon clip-coupon row wrapper: padding:5px 0px 5px 0px
                      if (/padding\s*:\s*5px/i.test(style)) return node;
                      if (node.getAttribute?.('data-csa-c-owner') === 'PromotionsDiscovery')
                        return node;
                      node = node.parentElement;
                    }
                    return el;
                  };
                  const isCheckoutPromoRow = (row) => {
                    if (!row) return false;
                    const text = (row.innerText || '').replace(/\s+/g, ' ');
                    const hasPspLink = !!row.querySelector(
                      'a.cxcwEmphasisLink[href*="/promotion/psp/"], a[href*="/promotion/psp/"], a#emphasisLink, #emphasisLink');
                    // Strict Amazon-promo signals only (bank coupons use different wording).
                    if (text.includes('عند الدفع')) return true;
                    if (text.includes('رمز العرض الترويجي')) return true;
                    if (row.querySelector('.a-alert-inline-success') && hasPspLink) return true;
                    // Clippable Amazon coupon row: CXCW promo message + PSP shop link.
                    if (row.querySelector('[id^="promoMessageCXCW"]') && hasPspLink) return true;
                    return false;
                  };
                  const markPromo = (el) => {
                    const target = promoRow(el);
                    if (!target || seenPromo.has(target) || !isCheckoutPromoRow(target)) return;
                    seenPromo.add(target);
                    if (outline(target, checkoutDiscountColor))
                      checkoutDiscount = target;
                  };
                  document.querySelectorAll(
                    '[id^="promoMessageCXCW"], [id^="doneMessage"], [id^="greenBadge"], [id^="clickMe"], [data-csa-c-action="clipPromotion"], .promoPriceBlockMessage [data-csa-c-owner="PromotionsDiscovery"], [data-csa-c-owner="PromotionsDiscovery"], .a-alert-inline-success')
                    .forEach(markPromo);

                  // Keep title + buybox in view; only nudge if shipper/promo is below the fold.
                  const scrollTarget = checkoutDiscount || seller;
                  if (scrollTarget) {
                    const sr = scrollTarget.getBoundingClientRect();
                    if (sr.bottom > window.innerHeight - 20 || sr.top < 0) {
                      const box = document.querySelector('#desktop_buybox, #buybox, #qualifiedBuybox');
                      if (box) {
                        box.scrollIntoView({ block: 'start', inline: 'nearest' });
                      } else {
                        scrollTarget.scrollIntoView({ block: 'end', inline: 'nearest' });
                      }
                    }
                  }
                }
                """);
        }

        private static async Task AddToCartAndOpenCartAsync(
            IPage page,
            string asin,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var addButton = page.Locator("#add-to-cart-button");
            if (await addButton.CountAsync() == 0)
                throw new InvalidOperationException("زر إضافة إلى السلة غير موجود.");

            await addButton.First.ClickAsync();

            // Sidesheet / "go to cart" controls are flaky (often never appear). Navigate
            // straight to the cart after add — same outcome, much more reliable.
            try
            {
                var goToCart = page.Locator(
                    "#sw-gtc a, #attach-sidesheet-view-cart-button");
                if (await goToCart.CountAsync() > 0)
                {
                    await goToCart.First.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
                }
                else
                {
                    await page.GotoAsync(
                        "https://www.amazon.eg/gp/cart/view.html?language=ar_AE&ref_=nav_cart",
                        new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45_000 });
                }
            }
            catch (PlaywrightException)
            {
                await page.GotoAsync(
                    "https://www.amazon.eg/gp/cart/view.html?language=ar_AE&ref_=nav_cart",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45_000 });
            }

            if (!await IsArabicUiAsync(page))
            {
                await page.GotoAsync(
                    "https://www.amazon.eg/-/ar/gp/cart/view.html?language=ar_AE",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45_000 });
            }

            await page.WaitForSelectorAsync(
                $"#sc-active-cart .sc-list-item[data-asin='{asin}'], #sc-active-cart .sc-list-item, #activeCartViewForm",
                new PageWaitForSelectorOptions { Timeout = 30_000 });
        }

        /// <summary>
        /// Arabic one-liner explaining why the cart path was chosen (null = use DP).
        /// </summary>
        private static string? DescribeCartReasonAr(
            CheckoutDiscountInfo? discount,
            decimal? pagePrice,
            decimal? alertPrice,
            bool pricesMatch)
        {
            if (discount is not null)
            {
                return discount.Kind.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? $"خصم تلقائي عند الدفع {discount.Percent}% (مش بانك)"
                    : $"كود خصم أمازون {discount.Percent}% (رمز العرض)";
            }

            // Null pagePrice is handled earlier (fail attempt → try proxy), not cart.
            if (!pricesMatch)
            {
                return
                    $"السعر مختلف — صفحة المنتج={pagePrice?.ToString(CultureInfo.InvariantCulture) ?? "null"}، قاعدة البيانات={alertPrice?.ToString(CultureInfo.InvariantCulture) ?? "null"}";
            }

            return null;
        }

        internal static bool PricesMatch(decimal pagePrice, decimal alertPrice) =>
            Math.Abs(pagePrice - alertPrice) < 0.05m;

        internal static decimal? ParsePrice(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            // First money token only (a11y label appends "توفير بنسبة 24").
            var match = Regex.Match(raw, @"[\d][\d.,]*");
            if (!match.Success)
                return null;

            var cleaned = Regex.Replace(match.Value, @"\.+", ".").Trim('.');
            if (string.IsNullOrWhiteSpace(cleaned))
                return null;

            if (cleaned.Contains(',') && cleaned.Contains('.'))
            {
                if (cleaned.LastIndexOf(',') > cleaned.LastIndexOf('.'))
                    cleaned = cleaned.Replace(".", "").Replace(',', '.');
                else
                    cleaned = cleaned.Replace(",", "");
            }
            else if (cleaned.Contains(','))
            {
                var parts = cleaned.Split(',');
                cleaned = parts.Length == 2 && parts[1].Length <= 2
                    ? cleaned.Replace(',', '.')
                    : cleaned.Replace(",", "");
            }

            return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        public static string BuildProductUrl(string asin) =>
            $"https://www.amazon.eg/dp/{asin.Trim()}?language=ar_AE";

        private static string DescribeFailure(Exception ex)
        {
            var msg = ex.Message ?? "";
            if (msg.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Chromium غير مثبت", StringComparison.OrdinalIgnoreCase))
            {
                return "Chromium غير مثبت. نفّذ playwright.ps1 install chromium ثم أعد تشغيل التطبيق.";
            }

            if (msg.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
                return "انتهت مهلة تحميل صفحة أمازون — أعد المحاولة.";

            if (ex is InvalidOperationException && !string.IsNullOrWhiteSpace(msg) && msg.Length <= 180)
                return msg;

            return "فشل تجهيز المنتج للنشر.";
        }

        private static string FormatExceptionDetail(Exception ex)
        {
            var type = ex.GetType().Name;
            var msg = (ex.Message ?? "").Trim();
            if (msg.Length > 1_200)
                msg = msg[..1_200] + "…";

            var inner = ex.InnerException?.Message?.Trim();
            if (string.IsNullOrWhiteSpace(inner))
                return $"{type}: {msg}";

            if (inner.Length > 400)
                inner = inner[..400] + "…";

            return $"{type}: {msg}\nInner: {inner}";
        }

        private static PrepareForPublishResult Fail(
            string error,
            string? detail = null,
            string? route = null,
            Guid correlationId = default) =>
            new()
            {
                Success = false,
                Error = error,
                Detail = detail,
                Route = route,
                CorrelationId = correlationId
            };
    }
}
