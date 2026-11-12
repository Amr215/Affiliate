using Affiliate.Noon;
using Affiliate.Options;
using Affiliate.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Affiliate.Controllers
{
    /// <summary>
    /// Public redirects:
    /// <c>/{code}</c> → Amazon product ASIN with publish Associates tag;
    /// <c>/d/{code}</c> → Amazon product ASIN with deals/site Associates tag;
    /// <c>/u/{code}</c> → stored non-product Amazon.eg URL with publish Associates tag;
    /// <c>/n/{11}</c> → classic Noon <c>N…</c> product SKU (codec, no DB);
    /// <c>/n/{8}</c> → stored Noon URL (brand/search/deals or partner / ZFC product pages).
    /// </summary>
    [AllowAnonymous]
    public class GoController(
        IOptions<TelegramOptions> options,
        AsinLinkCodec codec,
        NoonSkuLinkCodec noonSkuCodec,
        AmazonShortLinkService shortLinks,
        NoonShortLinkService noonShortLinks) : Controller
    {
        [HttpGet("/{code:regex(^[[0-9A-Za-z]]{{9}}$)}")]
        public IActionResult Go(string code) => RedirectToAmazonProduct(code, useDealsTag: false);

        /// <summary>Deals page / price-tracker buy links — uses <see cref="TelegramOptions.DealsAffiliateTag"/>.</summary>
        [HttpGet("/d/{code:regex(^[[0-9A-Za-z]]{{9}}$)}")]
        public IActionResult GoDeals(string code) => RedirectToAmazonProduct(code, useDealsTag: true);

        /// <summary>Keeps older <c>/go/…</c> channel links working (publish tag).</summary>
        [HttpGet("/go/{code:regex(^[[0-9A-Za-z]]{{9}}$)}")]
        public IActionResult GoLegacy(string code) => RedirectToAmazonProduct(code, useDealsTag: false);

        [HttpGet("/u/{code:regex(^[[0-9A-Za-z]]{{8}}$)}")]
        public async Task<IActionResult> GoUniversal(string code, CancellationToken cancellationToken)
        {
            var destination = await shortLinks.ResolveDestinationAsync(code, cancellationToken);
            if (string.IsNullOrWhiteSpace(destination))
                return NotFound();

            return RedirectNoIndex(AppendTag(destination, useDealsTag: false));
        }

        /// <summary>Noon product: opaque SKU code (not stored).</summary>
        [HttpGet("/n/{code:regex(^[[0-9A-Za-z]]{{11}}$)}")]
        public IActionResult GoNoonProduct(string code)
        {
            if (!noonSkuCodec.TryDecode(code, out var sku))
                return NotFound();

            return RedirectNoIndex(NoonPrepareForPublishService.BuildProductUrl(sku));
        }

        /// <summary>Noon general URL from <c>NoonShortLinks</c>.</summary>
        [HttpGet("/n/{code:regex(^[[0-9A-Za-z]]{{8}}$)}")]
        public async Task<IActionResult> GoNoon(string code, CancellationToken cancellationToken)
        {
            var destination = await noonShortLinks.ResolveDestinationAsync(code, cancellationToken);
            if (string.IsNullOrWhiteSpace(destination))
                return NotFound();

            return RedirectNoIndex(destination);
        }

        private IActionResult RedirectToAmazonProduct(string code, bool useDealsTag)
        {
            if (!codec.TryDecode(code, out var asin))
                return NotFound();

            return RedirectNoIndex(AppendTag($"https://www.amazon.eg/dp/{asin}", useDealsTag));
        }

        private IActionResult RedirectNoIndex(string destination)
        {
            Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            return Redirect(destination);
        }

        private string AppendTag(string destination, bool useDealsTag)
        {
            var tag = useDealsTag
                ? FirstNonEmpty(options.Value.DealsAffiliateTag, options.Value.AffiliateTag)
                : options.Value.AffiliateTag?.Trim();
            if (string.IsNullOrEmpty(tag))
                return destination;

            var sep = destination.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            return $"{destination}{sep}tag={Uri.EscapeDataString(tag)}";
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                var trimmed = value?.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    return trimmed;
            }

            return null;
        }
    }
}
