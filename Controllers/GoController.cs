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
    /// <c>/{code}</c> → Amazon product ASIN with Associates tag (codec, no DB);
    /// <c>/u/{code}</c> → stored non-product Amazon.eg URL with Associates tag;
    /// <c>/n/{11}</c> → Noon product SKU (codec, no DB);
    /// <c>/n/{8}</c> → stored non-product Noon URL.
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
        public IActionResult Go(string code) => RedirectToAmazonProduct(code);

        /// <summary>Keeps older <c>/go/…</c> channel links working.</summary>
        [HttpGet("/go/{code:regex(^[[0-9A-Za-z]]{{9}}$)}")]
        public IActionResult GoLegacy(string code) => RedirectToAmazonProduct(code);

        [HttpGet("/u/{code:regex(^[[0-9A-Za-z]]{{8}}$)}")]
        public async Task<IActionResult> GoUniversal(string code, CancellationToken cancellationToken)
        {
            var destination = await shortLinks.ResolveDestinationAsync(code, cancellationToken);
            if (string.IsNullOrWhiteSpace(destination))
                return NotFound();

            return Redirect(AppendTag(destination));
        }

        /// <summary>Noon product: opaque SKU code (not stored).</summary>
        [HttpGet("/n/{code:regex(^[[0-9A-Za-z]]{{11}}$)}")]
        public IActionResult GoNoonProduct(string code)
        {
            if (!noonSkuCodec.TryDecode(code, out var sku))
                return NotFound();

            return Redirect(NoonPrepareForPublishService.BuildProductUrl(sku));
        }

        /// <summary>Noon general URL from <c>NoonShortLinks</c>.</summary>
        [HttpGet("/n/{code:regex(^[[0-9A-Za-z]]{{8}}$)}")]
        public async Task<IActionResult> GoNoon(string code, CancellationToken cancellationToken)
        {
            var destination = await noonShortLinks.ResolveDestinationAsync(code, cancellationToken);
            if (string.IsNullOrWhiteSpace(destination))
                return NotFound();

            return Redirect(destination);
        }

        private IActionResult RedirectToAmazonProduct(string code)
        {
            if (!codec.TryDecode(code, out var asin))
                return NotFound();

            return Redirect(AppendTag($"https://www.amazon.eg/dp/{asin}"));
        }

        private string AppendTag(string destination)
        {
            var tag = options.Value.AffiliateTag?.Trim();
            if (string.IsNullOrEmpty(tag))
                return destination;

            var sep = destination.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            return $"{destination}{sep}tag={Uri.EscapeDataString(tag)}";
        }
    }
}
