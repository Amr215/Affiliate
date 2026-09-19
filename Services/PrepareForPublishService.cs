using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Affiliate.Services
{
    public interface IPrepareForPublishService
    {
        /// <summary>
        /// Opens Amazon.eg (no proxy), ensures Arabic UI, and returns a publish screenshot:
        /// highlighted DP when page price matches <paramref name="alertPrice"/>;
        /// otherwise adds to cart and returns a plain cart screenshot.
        /// </summary>
        Task<PrepareForPublishResult> PrepareAsync(
            string asin,
            decimal? alertPrice,
            CancellationToken cancellationToken = default);
    }

    public sealed class PrepareForPublishResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string? ProductName { get; init; }
        public string? ProductUrl { get; init; }
        public byte[]? ScreenshotPng { get; init; }
        public bool UsedCartScreenshot { get; init; }
    }

    public sealed class PrepareForPublishService : IPrepareForPublishService
    {
        private static readonly SemaphoreSlim BrowserGate = new(1, 1);
        private static int _browsersPathConfigured;

        private readonly ILogger<PrepareForPublishService> _logger;

        public PrepareForPublishService(ILogger<PrepareForPublishService> logger)
        {
            _logger = logger;
        }

        public async Task<PrepareForPublishResult> PrepareAsync(
            string asin,
            decimal? alertPrice,
            CancellationToken cancellationToken = default)
        {
            asin = (asin ?? string.Empty).Trim().ToUpperInvariant();
            if (asin.Length is < 8 or > 16 || asin.Any(c => !(char.IsLetterOrDigit(c))))
            {
                return Fail("ASIN غير صالح.");
            }

            EnsureBrowsersPath();

            var productUrl = BuildProductUrl(asin);
            await BrowserGate.WaitAsync(cancellationToken);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(3));

                return await CaptureAsync(asin, alertPrice, productUrl, cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Fail("انتهت مهلة تجهيز المنتج للنشر.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Prepare-for-publish failed for {Asin}", asin);
                return Fail(DescribeFailure(ex));
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

        private async Task<PrepareForPublishResult> CaptureAsync(
            string asin,
            decimal? alertPrice,
            string productUrl,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var playwright = await Playwright.CreateAsync();
            IBrowser browser;
            try
            {
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
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

            await using (browser)
            {
                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = 1366, Height = 1100 },
                    Locale = "ar-AE",
                    TimezoneId = "Africa/Cairo",
                    UserAgent =
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                    ExtraHTTPHeaders = new Dictionary<string, string>
                    {
                        ["Accept-Language"] = "ar-AE,ar;q=0.9,en-AE;q=0.5,en;q=0.4"
                    }
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
                    return Fail("أمازون طلب تحقق (captcha) — أعد المحاولة لاحقاً.");
                }

                var productName = await ReadProductTitleAsync(page) ?? asin;
                var pagePrice = await ReadDesktopPriceAsync(page);

                _logger.LogInformation(
                    "Prepare-for-publish {Asin}: pagePrice={PagePrice}, alertPrice={AlertPrice}, lang={Lang}",
                    asin, pagePrice, alertPrice, await ReadHtmlLangAsync(page));

                var pricesMatch = alertPrice.HasValue
                    && pagePrice.HasValue
                    && PricesMatch(pagePrice.Value, alertPrice.Value);

                byte[] screenshot;
                var usedCart = false;

                if (pricesMatch)
                {
                    await HighlightDesktopPublishTargetsAsync(page);
                    screenshot = await CapturePublishScreenshotAsync(page, cart: false);
                }
                else
                {
                    try
                    {
                        await AddToCartAndOpenCartAsync(page, asin, cancellationToken);
                        usedCart = true;
                        screenshot = await CapturePublishScreenshotAsync(page, cart: true);
                    }
                    catch (Exception cartEx)
                    {
                        // Cart often fails (interstitial / soft block). Fall back to highlighted DP.
                        _logger.LogWarning(cartEx,
                            "Prepare-for-publish cart path failed for {Asin}; falling back to DP screenshot",
                            asin);

                        await page.GotoAsync(productUrl, new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 45_000
                        });
                        await page.WaitForSelectorAsync(
                            "#productTitle, #add-to-cart-button, #corePrice_feature_div",
                            new PageWaitForSelectorOptions { Timeout = 30_000 });

                        await HighlightDesktopPublishTargetsAsync(page);
                        screenshot = await CapturePublishScreenshotAsync(page, cart: false);
                    }
                }

                return new PrepareForPublishResult
                {
                    Success = true,
                    ProductName = productName,
                    ProductUrl = productUrl,
                    ScreenshotPng = screenshot,
                    UsedCartScreenshot = usedCart
                };
            }
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

            await page.WaitForSelectorAsync("#productTitle, #add-to-cart-button, #corePrice_feature_div",
                new PageWaitForSelectorOptions { Timeout = 30_000 });

            if (await IsArabicUiAsync(page))
                return;

            var arPathUrl = $"https://www.amazon.eg/-/ar/dp/{asin}?language=ar_AE";
            await page.GotoAsync(arPathUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45_000
            });
            await page.WaitForSelectorAsync("#productTitle, #add-to-cart-button, #corePrice_feature_div",
                new PageWaitForSelectorOptions { Timeout = 30_000 });
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
            string[] selectors =
            [
                "#corePrice_feature_div .a-price.priceToPay .a-offscreen",
                "#corePriceDisplay_desktop_feature_div .a-price.priceToPay .a-offscreen",
                "#corePrice_feature_div span.a-offscreen",
                "#apex_desktop .a-price.priceToPay .a-offscreen",
                ".a-price.priceToPay .a-offscreen"
            ];

            foreach (var selector in selectors)
            {
                // Count on the base locator (no wait). Avoid .First before existence check —
                // .First.TextContentAsync waits up to the default timeout when missing.
                var loc = page.Locator(selector);
                if (await loc.CountAsync() == 0)
                    continue;

                try
                {
                    var text = await loc.First.TextContentAsync(new LocatorTextContentOptions { Timeout = 3_000 });
                    var parsed = ParsePrice(text);
                    if (parsed.HasValue)
                        return parsed;
                }
                catch (PlaywrightException)
                {
                    // try next selector
                }
            }

            var wholeLoc = page.Locator("#corePrice_feature_div .a-price.priceToPay .a-price-whole");
            if (await wholeLoc.CountAsync() > 0)
            {
                try
                {
                    var wholeText = await wholeLoc.First.TextContentAsync(new LocatorTextContentOptions { Timeout = 3_000 }) ?? "";
                    var fractionLoc = page.Locator("#corePrice_feature_div .a-price.priceToPay .a-price-fraction");
                    var fractionText = await fractionLoc.CountAsync() > 0
                        ? await fractionLoc.First.TextContentAsync(new LocatorTextContentOptions { Timeout = 2_000 }) ?? "00"
                        : "00";
                    return ParsePrice($"{wholeText}.{fractionText}");
                }
                catch (PlaywrightException)
                {
                    // ignore
                }
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
                  }
                  return bottoms.length ? Math.max(...bottoms) : window.innerHeight;
                }
                """,
                cart);

            const float maxHeight = 620f;
            const float pad = 16f;
            var height = Math.Clamp(contentBottom - y + pad, 280f, maxHeight);
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

        private static async Task HighlightDesktopPublishTargetsAsync(IPage page)
        {
            await page.EvaluateAsync(
                """
                () => {
                  const highlightColor = '#2E75B6';
                  const outline = (el) => {
                    if (!el) return false;
                    el.style.outline = `2px solid ${highlightColor}`;
                    el.style.outlineOffset = '2px';
                    el.style.boxShadow = 'none';
                    return true;
                  };

                  const outlined = new Set();
                  const outlinePrice = (el) => {
                    if (!el || outlined.has(el)) return;
                    outlined.add(el);
                    outline(el);
                  };

                  // Center column price (below title / rating).
                  outlinePrice(document.querySelector('#corePriceDisplay_desktop_feature_div'));

                  // Left buybox price (often #corePrice_feature_div inside buybox).
                  const buybox =
                    document.querySelector('#desktop_buybox') ||
                    document.querySelector('#buybox') ||
                    document.querySelector('#qualifiedBuybox');
                  if (buybox) {
                    outlinePrice(
                      buybox.querySelector('#corePrice_feature_div') ||
                      buybox.querySelector('#priceInsideBuyBox_feature_div') ||
                      buybox.querySelector('#desktop_unifiedPrice') ||
                      buybox.querySelector('.a-price.priceToPay')?.closest('[id], .a-section') ||
                      buybox.querySelector('.a-price.priceToPay') ||
                      buybox.querySelector('.a-price'));
                  }

                  // Fallbacks if a layout omits one of the above.
                  if (outlined.size === 0) {
                    outlinePrice(document.querySelector('#corePrice_feature_div'));
                    outlinePrice(document.querySelector('#centerCol .a-price.priceToPay'));
                  } else if (outlined.size === 1) {
                    outlinePrice(document.querySelector('#corePrice_feature_div'));
                    outlinePrice(document.querySelector('#corePriceDisplay_desktop_feature_div'));
                    outlinePrice(document.querySelector('#centerCol .a-price.priceToPay')?.closest('[id], .a-section'));
                  }

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

                  // Keep title + buybox in view; only nudge if shipper is below the fold.
                  if (seller) {
                    const sr = seller.getBoundingClientRect();
                    if (sr.bottom > window.innerHeight - 20 || sr.top < 0) {
                      const box = document.querySelector('#desktop_buybox, #buybox, #qualifiedBuybox');
                      if (box) {
                        box.scrollIntoView({ block: 'start', inline: 'nearest' });
                      } else {
                        seller.scrollIntoView({ block: 'end', inline: 'nearest' });
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

            try
            {
                var goToCart = page.Locator(
                    "#sw-gtc a, #attach-sidesheet-view-cart-button, #nav-cart, a[href*='/cart/']");
                await goToCart.First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 10_000
                });
                await goToCart.First.ClickAsync();
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

        internal static bool PricesMatch(decimal pagePrice, decimal alertPrice) =>
            Math.Abs(pagePrice - alertPrice) < 0.05m;

        internal static decimal? ParsePrice(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var cleaned = Regex.Replace(raw, @"[^\d.,]", "");
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

        private static PrepareForPublishResult Fail(string error) =>
            new() { Success = false, Error = error };
    }
}
