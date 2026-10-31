using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Affiliate.Noon.Models;
using Affiliate.Services;
using Microsoft.Playwright;

namespace Affiliate.Noon
{
    public interface INoonPrepareForPublishService
    {
        /// <summary>
        /// Opens a Noon Egypt product page via ISP SOCKS5 proxies only (never direct),
        /// using Playwright Chromium (full build, new headless mode). Captures a product-page screenshot with price + seller
        /// highlighted and the site navbar removed. No cart path.
        /// </summary>
        Task<NoonPrepareForPublishResult> PrepareAsync(
            string productUrlOrSku,
            decimal? alertPrice = null,
            Guid? correlationId = null,
            CancellationToken cancellationToken = default);
    }

    public sealed record NoonPrepareForPublishResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string? Detail { get; init; }
        public string? Route { get; init; }
        public string? Sku { get; init; }
        public string? ProductName { get; init; }
        public string? ProductUrl { get; init; }
        public decimal? ProductPrice { get; init; }
        public byte[]? ScreenshotPng { get; init; }
        public Guid CorrelationId { get; init; }
    }

    public sealed class NoonPrepareForPublishService : INoonPrepareForPublishService
    {
        private const int MaxProxyAttempts = 20;
        // Noon's page is heavy and needs full Chromium; each parallel attempt is a full render.
        private const int ProxyParallelism = 2;
        // Wide enough for Noon’s RTL layout (gallery right + seller left) without clipping.
        private const int PublishViewportWidth = 1280;
        private const int PublishViewportHeight = 1700;
        private const int PublishImageMaxWidth = 1280;

        private static readonly SemaphoreSlim BrowserGate = new(1, 1);
        private static readonly SemaphoreSlim SharedBrowserLock = new(1, 1);
        private static int _browsersPathConfigured;
        private static IPlaywright? _sharedPlaywright;
        private static IBrowser? _sharedBrowser;
        private static (DateTime At, Exception Error)? _lastLaunchFailure;

        private static readonly Regex SkuInUrl = new(
            @"/(N[A-Z0-9]{5,})(?:/p/?|/|\?|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Analytics / ads / chat widgets: not needed for the screenshot, only cost CPU and bandwidth.
        private static readonly Regex BlockedThirdParty = new(
            @"google-analytics|googletagmanager|doubleclick|googlesyndication|googleadservices|" +
            @"facebook\.(net|com)|connect\.facebook|tiktok|snapchat|sc-static|hotjar|clarity\.ms|" +
            @"criteo|appsflyer|branch\.io|adjust\.com|bing\.com|twitter\.com|ads-twitter|" +
            @"newrelic|nr-data|intercom|zendesk|freshchat|optimizely|segment\.(io|com)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BareSku = new(
            @"^N[A-Z0-9]{5,}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IIspProxyService _ispProxy;
        private readonly NoonProxyBlocklist _blocklist;
        private readonly INoonPublishAuditLogger _audit;
        private readonly ILogger<NoonPrepareForPublishService> _logger;

        public NoonPrepareForPublishService(
            IIspProxyService ispProxy,
            NoonProxyBlocklist blocklist,
            INoonPublishAuditLogger audit,
            ILogger<NoonPrepareForPublishService> logger)
        {
            _ispProxy = ispProxy;
            _blocklist = blocklist;
            _audit = audit;
            _logger = logger;
        }

        public async Task<NoonPrepareForPublishResult> PrepareAsync(
            string productUrlOrSku,
            decimal? alertPrice = null,
            Guid? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            var corr = correlationId ?? Guid.NewGuid();
            if (!TryResolveSkuAndUrl(productUrlOrSku, out var sku, out var productUrl))
            {
                return Fail("رابط أو SKU نون غير صالح.", correlationId: corr);
            }

            EnsureBrowsersPath();

            await BrowserGate.WaitAsync(cancellationToken);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(2));

                var result = await CaptureWithProxiesAsync(sku, alertPrice, productUrl, corr, cts.Token);
                _audit.Log(corr, sku,
                    result.Success ? NoonPublishAuditEvent.Finished : NoonPublishAuditEvent.Failed,
                    route: result.Route,
                    pageKind: result.Success ? "Dp" : null,
                    success: result.Success,
                    detail: result.Success ? null : Truncate(result.Detail ?? result.Error));
                return result with { CorrelationId = corr, Sku = sku };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _audit.Log(corr, sku, NoonPublishAuditEvent.Failed,
                    success: false, detail: "Overall prepare timeout (2 minutes)");
                return Fail(
                    "انتهت مهلة تجهيز منتج نون للنشر.",
                    "Overall prepare timeout (2 minutes).",
                    correlationId: corr,
                    sku: sku);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Noon prepare-for-publish failed for {Sku}", sku);
                _audit.Log(corr, sku, NoonPublishAuditEvent.Failed,
                    success: false, detail: Truncate(FormatExceptionDetail(ex)));
                return Fail(DescribeFailure(ex), FormatExceptionDetail(ex), correlationId: corr, sku: sku);
            }
            finally
            {
                BrowserGate.Release();
            }
        }

        private async Task<NoonPrepareForPublishResult> CaptureWithProxiesAsync(
            string sku,
            decimal? alertPrice,
            string productUrl,
            Guid correlationId,
            CancellationToken cancellationToken)
        {
            var proxyProbe = _ispProxy.GetEndpoint();
            if (!proxyProbe.UseProxy)
            {
                return Fail(
                    "بروكسيات ISP غير مفعّلة — نون يُجهَّز عبر البروكسي فقط.",
                    "ISP proxies disabled or empty.",
                    correlationId: correlationId,
                    sku: sku);
            }

            NoonPrepareForPublishResult? lastFailure = null;
            var triedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var proxiesTried = 0;

            while (proxiesTried < MaxProxyAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batchSize = Math.Min(ProxyParallelism, MaxProxyAttempts - proxiesTried);
                var batch = new List<IspProxyEndpoint>(batchSize);
                for (var i = 0; i < batchSize * 3 && batch.Count < batchSize; i++)
                {
                    var ep = _ispProxy.GetEndpoint();
                    if (!ep.UseProxy || string.IsNullOrWhiteSpace(ep.Host))
                        continue;
                    if (_blocklist.IsBlocked(ep.Key))
                        continue;
                    if (!triedKeys.Add(ep.Key))
                        continue;
                    batch.Add(ep);
                }

                if (batch.Count == 0)
                {
                    if (lastFailure is not null)
                        return lastFailure;
                    return Fail(
                        "كل البروكسيات محظورة أو مستخدمة — أعد المحاولة.",
                        "No unused unblocked proxies left.",
                        correlationId: correlationId,
                        sku: sku);
                }

                proxiesTried += batch.Count;
                var (winner, failures) = await RaceProxyBatchAsync(
                    sku, alertPrice, productUrl, batch, correlationId, cancellationToken);

                foreach (var f in failures)
                    lastFailure = f;

                if (winner is { Success: true })
                    return winner;
            }

            return lastFailure ?? Fail(
                "فشل تجهيز منتج نون للنشر بعد عدة بروكسيات.",
                $"Exhausted {MaxProxyAttempts} proxy attempts.",
                correlationId: correlationId,
                sku: sku);
        }

        private async Task<(NoonPrepareForPublishResult? Winner, List<NoonPrepareForPublishResult> Failures)> RaceProxyBatchAsync(
            string sku,
            decimal? alertPrice,
            string productUrl,
            IReadOnlyList<IspProxyEndpoint> batch,
            Guid correlationId,
            CancellationToken cancellationToken)
        {
            using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var tasks = batch.Select(ep =>
                TryCaptureOnceAsync(sku, alertPrice, productUrl, ep, correlationId, raceCts.Token)).ToList();

            var failures = new List<NoonPrepareForPublishResult>();
            NoonPrepareForPublishResult? winner = null;

            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks);
                tasks.Remove(finished);
                NoonPrepareForPublishResult result;
                try
                {
                    result = await finished;
                }
                catch (OperationCanceledException) when (raceCts.IsCancellationRequested && winner is not null)
                {
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(Fail(DescribeFailure(ex), FormatExceptionDetail(ex), correlationId: correlationId, sku: sku));
                    continue;
                }

                if (result.Success)
                {
                    winner = result;
                    raceCts.Cancel();
                    break;
                }

                failures.Add(result);
            }

            return (winner, failures);
        }

        private async Task<NoonPrepareForPublishResult> TryCaptureOnceAsync(
            string sku,
            decimal? alertPrice,
            string productUrl,
            IspProxyEndpoint proxy,
            Guid correlationId,
            CancellationToken cancellationToken)
        {
            var route = proxy.Key;
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await CaptureOnceAsync(sku, alertPrice, productUrl, proxy, correlationId, cancellationToken);
                _audit.Log(correlationId, sku, NoonPublishAuditEvent.Attempt,
                    route: route,
                    proxyHost: proxy.Host,
                    proxyPort: proxy.Port,
                    pageKind: result.Success ? "Dp" : null,
                    success: result.Success,
                    durationMs: (int)sw.ElapsedMilliseconds,
                    detail: Truncate(result.Detail ?? result.Error));

                if (result.Success)
                    _blocklist.ReportSuccess(proxy.Key);
                else if (_blocklist.ReportFailure(proxy.Key))
                    _logger.LogWarning("Noon publish: proxy {Proxy} blocked after repeated failures", proxy.Key);

                return result with { CorrelationId = correlationId, Sku = sku, Route = route };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Context was closed because another proxy won; not this proxy's fault.
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Noon prepare attempt failed for {Sku} via {Route}", sku, route);
                if (_blocklist.ReportFailure(proxy.Key))
                    _logger.LogWarning("Noon publish: proxy {Proxy} blocked after exception", proxy.Key);

                _audit.Log(correlationId, sku, NoonPublishAuditEvent.Attempt,
                    route: route,
                    proxyHost: proxy.Host,
                    proxyPort: proxy.Port,
                    success: false,
                    durationMs: (int)sw.ElapsedMilliseconds,
                    detail: Truncate(FormatExceptionDetail(ex)));
                return Fail(DescribeFailure(ex), FormatExceptionDetail(ex), route, correlationId, sku);
            }
        }

        private async Task<NoonPrepareForPublishResult> CaptureOnceAsync(
            string sku,
            decimal? alertPrice,
            string productUrl,
            IspProxyEndpoint proxy,
            Guid correlationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var route = proxy.Key;

            // Chromium cannot auth SOCKS5; bridge local no-auth SOCKS5 → upstream Webshare.
            await using var socksBridge = LocalSocks5Bridge.Start(
                proxy.Host!, proxy.Port, proxy.Username, proxy.Password);
            var playwrightProxy = new Proxy { Server = socksBridge.PlaywrightServer };

            var browser = await GetSharedChromiumAsync();
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1000, Height = 1100 },
                DeviceScaleFactor = 2,
                Locale = "ar-EG",
                TimezoneId = "Africa/Cairo",
                UserAgent =
                    $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{browser.Version.Split('.')[0]}.0.0.0 Safari/537.36",
                ExtraHTTPHeaders = new Dictionary<string, string>
                {
                    ["Accept-Language"] = "ar-EG,ar;q=0.9,en-US;q=0.5,en;q=0.4"
                },
                Proxy = playwrightProxy
            });

            // Playwright calls ignore the token, so a losing attempt would keep rendering for up to 45s.
            await using var closeOnCancel = cancellationToken.Register(() => _ = CloseQuietlyAsync(context));

            await context.RouteAsync("**/*", async route =>
            {
                var request = route.Request;
                // Fonts stay: the Arabic Cairo font is part of how the screenshot looks.
                var isNoonAsset = request.Url.Contains("noon.com/", StringComparison.OrdinalIgnoreCase) ||
                                  request.Url.Contains("nooncdn.com/", StringComparison.OrdinalIgnoreCase);
                if (request.ResourceType == "media" ||
                    (!isNoonAsset && BlockedThirdParty.IsMatch(request.Url)))
                {
                    await route.AbortAsync();
                    return;
                }

                await route.ContinueAsync();
            });

            await context.AddCookiesAsync(
            [
                new Cookie
                {
                    Name = "nloc",
                    Value = "ar-eg",
                    Domain = ".noon.com",
                    Path = "/"
                },
                new Cookie
                {
                    Name = "visitor_id",
                    Value = Guid.NewGuid().ToString("N"),
                    Domain = ".noon.com",
                    Path = "/"
                }
            ]);

            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(45_000);

            await page.GotoAsync(productUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45_000
            });

            if (await LooksBlockedAsync(page))
            {
                return Fail(
                    "نون طلب تحقق / حظر — جرب بروكسي آخر.",
                    $"Captcha/block via {route}",
                    route,
                    correlationId,
                    sku);
            }

            await WaitForProductSignalAsync(page);

            var productName = await ReadProductTitleAsync(page) ?? sku;
            var pagePrice = await ReadPriceAsync(page);

            _logger.LogInformation(
                "Noon prepare {Sku} via {Route}: pagePrice={PagePrice}, alertPrice={AlertPrice}",
                sku, route, pagePrice, alertPrice);

            if (pagePrice is null)
            {
                return Fail(
                    "ما قدرناش نقرأ سعر صفحة نون — هنحاول بروكسي تاني.",
                    "pagePrice=null; retry via proxy",
                    route,
                    correlationId,
                    sku);
            }

            // Prefer the canonical Arabic URL from the page when available.
            var finalUrl = await ReadCanonicalUrlAsync(page) ?? productUrl;

            await UseNarrowPublishLayoutAsync(page);
            await HideNavbarAsync(page);
            await WaitForMainProductImageAsync(page);
            await HideNavbarAsync(page);
            await HighlightPublishTargetsAsync(page);
            var screenshot = await CapturePublishScreenshotAsync(page);

            return new NoonPrepareForPublishResult
            {
                Success = true,
                Route = route,
                CorrelationId = correlationId,
                Sku = sku,
                ProductName = productName,
                ProductUrl = finalUrl,
                ProductPrice = pagePrice,
                ScreenshotPng = screenshot,
                Detail = "سبب المسار: صفحة المنتج فقط (بدون سلة)"
            };
        }

        private static async Task CloseQuietlyAsync(IBrowserContext context)
        {
            try { await context.CloseAsync(); }
            catch { /* already closed */ }
        }

        private static async Task<IBrowser> GetSharedChromiumAsync()
        {
            if (_sharedBrowser is { IsConnected: true })
                return _sharedBrowser;

            await SharedBrowserLock.WaitAsync();
            try
            {
                if (_sharedBrowser is { IsConnected: true })
                    return _sharedBrowser;

                // Launch failures are not proxy-related; don't make every parallel attempt re-wait.
                if (_lastLaunchFailure is { } failure &&
                    DateTime.UtcNow - failure.At < TimeSpan.FromSeconds(30))
                {
                    throw new InvalidOperationException(
                        "تعذر تشغيل Chromium على السيرفر.", failure.Error);
                }

                if (_sharedBrowser is not null)
                {
                    try { await _sharedBrowser.DisposeAsync(); }
                    catch { /* already dead */ }
                    _sharedBrowser = null;
                }

                _sharedPlaywright ??= await Playwright.CreateAsync();
                try
                {
                    // Channel "chromium" = full Chromium in new headless mode. Noon rejects the
                    // default chrome-headless-shell (ERR_HTTP2_PROTOCOL_ERROR) but accepts this one.
                    _sharedBrowser = await _sharedPlaywright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = true,
                        Channel = "chromium",
                        Timeout = 60_000,
                        Args =
                        [
                            "--disable-blink-features=AutomationControlled",
                            "--no-sandbox",
                            "--disable-dev-shm-usage",
                            "--disable-gpu",
                            "--disable-extensions",
                            "--disable-background-networking",
                            "--mute-audio"
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
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
                {
                    _lastLaunchFailure = (DateTime.UtcNow, ex);
                    throw;
                }

                _lastLaunchFailure = null;
                return _sharedBrowser;
            }
            finally
            {
                SharedBrowserLock.Release();
            }
        }

        private static void EnsureBrowsersPath()
        {
            if (Interlocked.Exchange(ref _browsersPathConfigured, 1) == 1)
                return;

            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ms-playwright");

            var current = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
            var useLocal = string.IsNullOrWhiteSpace(current)
                           || current.Contains("cursor-sandbox-cache", StringComparison.OrdinalIgnoreCase)
                           || !Directory.Exists(current);

            if (useLocal && Directory.Exists(local))
                Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", local);
        }

        private static async Task WaitForProductSignalAsync(IPage page)
        {
            try
            {
                await page.WaitForSelectorAsync(
                    "h1[data-qa^='pdp-name'], [data-qa^='pdp-price'], .priceNow, [class*='priceNow']",
                    new PageWaitForSelectorOptions { Timeout = 30_000 });
            }
            catch (PlaywrightException)
            {
                // Fall through — blocked / empty page handled by price read.
            }

            await page.WaitForTimeoutAsync(800);
        }

        private static async Task<bool> LooksBlockedAsync(IPage page)
        {
            var title = (await page.TitleAsync())?.ToLowerInvariant() ?? "";
            if (title.Contains("access denied") || title.Contains("captcha") || title.Contains("robot"))
                return true;

            return await page.EvaluateAsync<bool>(
                """
                () => {
                  const t = (document.body?.innerText || '').toLowerCase();
                  return t.includes('access denied') ||
                         t.includes('verify you are human') ||
                         t.includes('cf-challenge') ||
                         !!document.querySelector('#challenge-running, .cf-browser-verification');
                }
                """);
        }

        private static async Task<string?> ReadProductTitleAsync(IPage page)
        {
            var text = await page.EvaluateAsync<string?>(
                """
                () => {
                  const el =
                    document.querySelector('h1[data-qa^="pdp-name"]') ||
                    document.querySelector('[data-qa="product-name"]') ||
                    document.querySelector('h1');
                  const t = (el?.innerText || '').replace(/\s+/g, ' ').trim();
                  return t || null;
                }
                """);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        private static async Task<decimal?> ReadPriceAsync(IPage page)
        {
            var raw = await page.EvaluateAsync<string?>(
                """
                () => {
                  const pick = (el) => (el?.innerText || el?.textContent || '').replace(/\s+/g, ' ').trim();
                  const now =
                    document.querySelector('[data-qa^="pdp-price"] .priceNow') ||
                    document.querySelector('.priceNow') ||
                    document.querySelector('[class*="priceNow"]') ||
                    document.querySelector('[data-qa^="pdp-price"] strong.amount') ||
                    document.querySelector('[data-qa^="pdp-price"]');
                  return pick(now) || null;
                }
                """);
            return ParsePrice(raw);
        }

        private static async Task<string?> ReadCanonicalUrlAsync(IPage page)
        {
            var href = await page.EvaluateAsync<string?>(
                """
                () => {
                  const link = document.querySelector('link[rel="canonical"]');
                  return link?.href || location.href || null;
                }
                """);
            if (string.IsNullOrWhiteSpace(href))
                return null;
            if (!href.Contains("noon.com", StringComparison.OrdinalIgnoreCase))
                return null;
            return href.Split('#')[0];
        }

        private static async Task UseNarrowPublishLayoutAsync(IPage page)
        {
            await page.SetViewportSizeAsync(PublishViewportWidth, PublishViewportHeight);
            await page.EvaluateAsync(
                """
                () => {
                  const style = document.createElement('style');
                  style.textContent = `
                    html, body, #__next, main, header, nav {
                      min-width: 0 !important;
                      max-width: 100% !important;
                    }
                    *, *::before, *::after {
                      transition: none !important;
                      animation-play-state: paused !important;
                    }
                  `;
                  document.head.appendChild(style);

                  window.scrollTo(0, 0);
                }
                """);
            await page.WaitForTimeoutAsync(400);
        }

        private static async Task HideNavbarAsync(IPage page)
        {
            await page.EvaluateAsync(
                """
                () => {
                  const hide = (el) => {
                    if (!el) return;
                    el.style.setProperty('display', 'none', 'important');
                    el.style.setProperty('visibility', 'hidden', 'important');
                    el.style.setProperty('height', '0', 'important');
                    el.style.setProperty('max-height', '0', 'important');
                    el.style.setProperty('overflow', 'hidden', 'important');
                    el.style.setProperty('pointer-events', 'none', 'important');
                  };

                  // Stylesheet survives React re-rendering the header after a slow (proxied) load.
                  if (!document.getElementById('aff-hide-style')) {
                    const style = document.createElement('style');
                    style.id = 'aff-hide-style';
                    style.textContent =
                      'header, nav, [data-qa="header"], [data-aff-hide] { display: none !important; }';
                    document.head.appendChild(style);
                  }

                  // Category menu strip (one <li data-qa="btn_main_menu_…"> per category):
                  // hide its full-width bar, not just the list.
                  const menuItem = document.querySelector('[data-qa^="btn_main_menu_"]');
                  if (menuItem) {
                    let bar = menuItem.parentElement;
                    while (bar?.parentElement && bar.parentElement !== document.body &&
                           bar.parentElement.getBoundingClientRect().height < 120)
                      bar = bar.parentElement;
                    bar?.setAttribute('data-aff-hide', '1');
                  }

                  // Site chrome — keep the product column only.
                  document.querySelectorAll(
                    'header, nav, [data-qa="header"], [class*="headerWrapper"], [class*="HeaderWrapper"],' +
                    '[class*="topNav"], [class*="TopNav"], [class*="navbar"], [class*="NavBar"],' +
                    '[data-qa="footer"], footer, [class*="cookie"], [id*="cookie"],' +
                    '[class*="PromoBanner"], [class*="promoBanner"], [class*="announcement"]'
                  ).forEach(hide);

                  // Sticky search / category strips often rendered outside <header>.
                  document.querySelectorAll('div, section').forEach(el => {
                    const role = (el.getAttribute('role') || '').toLowerCase();
                    if (role === 'banner' || role === 'navigation')
                      hide(el);
                  });
                }
                """);
            await page.WaitForTimeoutAsync(250);
        }

        private static async Task HighlightPublishTargetsAsync(IPage page)
        {
            await page.EvaluateAsync(
                """
                () => {
                  const highlightColor = '#2E75B6';
                  // Stylesheet + marker attribute: React re-renders drop inline styles.
                  // Inset outline: Noon's cards use overflow:hidden, which clips an outer one.
                  if (!document.getElementById('aff-hl-style')) {
                    const style = document.createElement('style');
                    style.id = 'aff-hl-style';
                    style.textContent =
                      `[data-aff-hl] { outline: 2px solid ${highlightColor} !important; outline-offset: -2px !important; }`;
                    document.head.appendChild(style);
                  }
                  const outline = (el) => {
                    if (!el) return false;
                    el.setAttribute('data-aff-hl', '1');
                    return true;
                  };

                  // Price row: current price + was price + saving badge.
                  const priceNow = document.querySelector('[data-qa="div-price-now"]');
                  let priceRow = priceNow;
                  while (priceRow?.parentElement) {
                    const r = priceRow.parentElement.getBoundingClientRect();
                    if (r.height > 50 || r.width > 520) break;
                    priceRow = priceRow.parentElement;
                  }
                  const price =
                    priceRow ||
                    document.querySelector('[data-qa^="pdp-price"]') ||
                    document.querySelector('.priceNow')?.closest('[data-qa], div, section') ||
                    document.querySelector('[class*="priceNow"]')?.closest('div');
                  outline(price);

                  // Seller / partner store
                  const sellerText = (el) => (el?.innerText || '').replace(/\s+/g, ' ');
                  const looksLikeSeller = (el) => {
                    const t = sellerText(el);
                    return /البائع|البائعين|يباع|البيع عن|البيع عبر|Sold by|Seller|Partner|شريك|متجر|Store|sold by/i.test(t);
                  };

                  // Compact "Sold by" card: last (innermost) match that is still card-sized.
                  let soldByCard = null;
                  for (const el of document.querySelectorAll('div')) {
                    const t = sellerText(el);
                    if (!/يتم البيع عبر|Sold by/i.test(t) || t.length > 300) continue;
                    const r = el.getBoundingClientRect();
                    if (r.width >= 180 && r.height >= 30 && r.height <= 200) soldByCard = el;
                  }

                  let seller =
                    soldByCard ||
                    document.querySelector('[data-qa*="seller"]') ||
                    document.querySelector('[data-qa*="partner"]') ||
                    document.querySelector('[data-qa*="store"]');

                  if (!looksLikeSeller(seller)) {
                    seller = null;
                    const candidates = document.querySelectorAll('div, span, a, p');
                    for (const el of candidates) {
                      if (!looksLikeSeller(el)) continue;
                      // Prefer a compact container around the label, not the whole page.
                      seller =
                        el.closest('[data-qa]') ||
                        el.closest('section') ||
                        el.parentElement ||
                        el;
                      if (seller && seller.innerText && seller.innerText.length < 400)
                        break;
                    }
                  }

                  outline(seller);

                  const scrollTarget = seller || price;
                  if (scrollTarget) {
                    const sr = scrollTarget.getBoundingClientRect();
                    if (sr.bottom > window.innerHeight - 20 || sr.top < 0)
                      scrollTarget.scrollIntoView({ block: 'nearest', inline: 'nearest' });
                  }
                }
                """);
        }

        /// <summary>
        /// The main gallery image loads lazily (spinner first), and slowly through a proxy.
        /// Wait until the large image in the top area is decoded; give up quietly after the timeout.
        /// </summary>
        private static async Task WaitForMainProductImageAsync(IPage page)
        {
            await page.EvaluateAsync(
                """
                () => {
                  window.scrollTo(0, 0);
                  document.querySelectorAll('img[loading="lazy"]').forEach(img => img.loading = 'eager');
                }
                """);

            try
            {
                await page.WaitForFunctionAsync(
                    """
                    () => {
                      const imgs = [...document.querySelectorAll('img')].filter(img => {
                        const r = img.getBoundingClientRect();
                        return r.width >= 200 && r.height >= 200 && r.top < window.innerHeight && r.bottom > 0;
                      });
                      return imgs.some(img => img.complete && img.naturalWidth > 0);
                    }
                    """,
                    null,
                    new PageWaitForFunctionOptions { Timeout = 12_000, PollingInterval = 250 });

                // Let the gallery swap the spinner for the decoded image.
                await page.WaitForTimeoutAsync(600);
            }
            catch (TimeoutException)
            {
            }
        }

        /// <summary>
        /// Downscales the full-layout screenshot so it is narrower in pixels while keeping
        /// Noon's side-by-side columns intact (a narrower viewport truncates text instead).
        /// </summary>
        private static byte[] ShrinkToPublishWidth(byte[] png)
        {
            using var source = SkiaSharp.SKBitmap.Decode(png);
            if (source is null || source.Width <= PublishImageMaxWidth)
                return png;

            var scale = (float)PublishImageMaxWidth / source.Width;
            var info = new SkiaSharp.SKImageInfo(PublishImageMaxWidth, (int)MathF.Round(source.Height * scale));
            using var resized = source.Resize(info, SkiaSharp.SKFilterQuality.High);
            if (resized is null)
                return png;

            using var image = SkiaSharp.SKImage.FromBitmap(resized);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private static async Task<byte[]> CapturePublishScreenshotAsync(IPage page)
        {
            // Keep scroll at top so the RTL seller column (often above the title) stays in frame.
            await page.EvaluateAsync("() => window.scrollTo(0, 0)");
            await page.WaitForTimeoutAsync(150);

            var viewport = page.ViewportSize;
            if (viewport is null)
            {
                return await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Type = ScreenshotType.Png,
                    FullPage = false
                });
            }

            // Seller column often starts above the title on Noon RTL — include both.
            var bounds = await page.EvaluateAsync<float[]>(
                """
                () => {
                  const tops = [];
                  const bottoms = [];
                  const add = (el) => {
                    if (!el) return;
                    const r = el.getBoundingClientRect();
                    if (r.width < 20 || r.height < 8 || r.height > 900) return;
                    tops.push(r.top);
                    bottoms.push(r.bottom);
                  };

                  add(document.querySelector('h1[data-qa^="pdp-name"]'));
                  add(document.querySelector('[data-qa="product-name"]'));
                  add(document.querySelector('h1'));
                  add(document.querySelector('[data-qa^="pdp-price"]'));
                  add(document.querySelector('.priceNow'));
                  add(document.querySelector('[class*="priceNow"]'));
                  add(document.querySelector('[data-qa="div-price-now"]'));

                  // Main gallery photo, so it isn't cut. Gallery slides share one row, so any
                  // slide gives its vertical extent even mid-swipe or off-screen horizontally.
                  // Height >= 250 skips short promo banners in the details column.
                  for (const img of document.querySelectorAll('img')) {
                    const r = img.getBoundingClientRect();
                    if (r.width < 200 || r.height < 250 || r.height > 900) continue;
                    if (r.top > 900 || r.bottom < 0) continue;
                    tops.push(r.top);
                    bottoms.push(r.bottom);
                  }

                  const top = tops.length ? Math.min(...tops) : 0;
                  const bottom = bottoms.length ? Math.max(...bottoms) : window.innerHeight * 0.7;

                  // Seller column is left out: start at the title/price column (widest
                  // single-column ancestor of the title) and run to the gallery's right edge.
                  const h1 = document.querySelector('h1[data-qa^="pdp-name"]') || document.querySelector('h1');
                  let left = 0;
                  if (h1) {
                    const titleWidth = h1.getBoundingClientRect().width;
                    let col = h1;
                    for (let p = h1.parentElement; p; p = p.parentElement) {
                      if (p.getBoundingClientRect().width > titleWidth + 12) break;
                      col = p;
                    }
                    left = Math.max(0, col.getBoundingClientRect().left - 16);
                  }
                  const pdp = document.querySelector('[data-qa="pdp-container"]')?.getBoundingClientRect();
                  const right = pdp && pdp.width > 600 ? Math.min(window.innerWidth, pdp.right + 8) : window.innerWidth;
                  return [top, bottom, left, right];
                }
                """);

            var contentTop = bounds is { Length: >= 2 } ? bounds[0] : 0f;
            var contentBottom = bounds is { Length: >= 2 } ? bounds[1] : viewport.Height * 0.7f;
            var contentLeft = bounds is { Length: >= 4 } ? bounds[2] : 0f;
            var contentRight = bounds is { Length: >= 4 } ? bounds[3] : viewport.Width;

            const float topPad = 26f;
            const float bottomPad = 20f;
            const float maxHeight = 1450f;
            var y = Math.Max(0f, contentTop - topPad);
            var height = Math.Clamp(contentBottom - y + bottomPad, 400f, maxHeight);
            height = Math.Min(height, viewport.Height - y);

            var png = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Png,
                FullPage = false,
                Clip = new Clip
                {
                    X = contentLeft,
                    Y = y,
                    Width = contentRight - contentLeft,
                    Height = height
                }
            });

            return ShrinkToPublishWidth(png);
        }

        public static bool TryResolveSkuAndUrl(string input, out string sku, out string productUrl)
        {
            sku = "";
            productUrl = "";
            var text = (input ?? "").Trim();
            if (text.Length == 0)
                return false;

            if (BareSku.IsMatch(text))
            {
                sku = text.ToUpperInvariant();
                productUrl = BuildProductUrl(sku);
                return true;
            }

            var m = SkuInUrl.Match(text);
            if (!m.Success)
                return false;

            sku = m.Groups[1].Value.ToUpperInvariant();

            // Prefer the posted URL when it already points at noon.com; force Arabic locale.
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                uri.Host.EndsWith("noon.com", StringComparison.OrdinalIgnoreCase))
            {
                productUrl = ToArabicLocaleUrl(text);
                if (!productUrl.Contains("/egypt-", StringComparison.OrdinalIgnoreCase))
                    productUrl = BuildProductUrl(sku);
            }
            else
            {
                productUrl = BuildProductUrl(sku);
            }

            return true;
        }

        public static string BuildProductUrl(string sku) =>
            $"https://www.noon.com/egypt-ar/{sku.Trim()}/p/";

        /// <summary>
        /// Rewrites noon.com locale segments to Arabic Egypt (<c>egypt-ar</c>), e.g.
        /// <c>/egypt-en/…</c> → <c>/egypt-ar/…</c>. Non-noon URLs are returned unchanged.
        /// </summary>
        public static string ToArabicLocaleUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
                !uri.Host.EndsWith("noon.com", StringComparison.OrdinalIgnoreCase))
            {
                return url?.Trim() ?? "";
            }

            var path = Regex.Replace(
                uri.AbsolutePath,
                @"^/(egypt|saudi|uae)-(en|ar)",
                "/egypt-ar",
                RegexOptions.IgnoreCase);
            return $"https://www.noon.com{path}{uri.Query}";
        }

        internal static decimal? ParsePrice(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

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

        private static string DescribeFailure(Exception ex)
        {
            var msg = ex.Message ?? "";
            if (msg.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Chromium غير مثبت", StringComparison.OrdinalIgnoreCase))
            {
                return "Chromium غير مثبت. نفّذ playwright.ps1 install chromium ثم أعد تشغيل التطبيق.";
            }

            if (msg.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
                return "انتهت مهلة تحميل صفحة نون — أعد المحاولة.";

            if (ex is InvalidOperationException && !string.IsNullOrWhiteSpace(msg) && msg.Length <= 180)
                return msg;

            return "فشل تجهيز منتج نون للنشر.";
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

        private static string? Truncate(string? s) =>
            s is { Length: > 500 } ? s[..500] : s;

        private static NoonPrepareForPublishResult Fail(
            string error,
            string? detail = null,
            string? route = null,
            Guid correlationId = default,
            string? sku = null) =>
            new()
            {
                Success = false,
                Error = error,
                Detail = detail,
                Route = route,
                CorrelationId = correlationId,
                Sku = sku
            };
    }
}
