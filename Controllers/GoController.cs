using Affiliate.Options;
using Affiliate.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Affiliate.Controllers
{
    /// <summary>
    /// Public Amazon redirects:
    /// <c>/{code}</c> → product ASIN with Associates tag;
    /// <c>/u/{code}</c> → stored non-product Amazon.eg URL with Associates tag.
    /// </summary>
    [AllowAnonymous]
    public class GoController(
        IOptions<TelegramOptions> options,
        AsinLinkCodec codec,
        AmazonShortLinkService shortLinks) : Controller
    {
        [HttpGet("/{code:regex(^[[0-9A-Za-z]]{{9}}$)}")]
        public IActionResult Go(string code) => RedirectToProduct(code);

        /// <summary>Keeps older <c>/go/…</c> channel links working.</summary>
        [HttpGet("/go/{code:regex(^[[0-9A-Za-z]]{{9}}$)}")]
        public IActionResult GoLegacy(string code) => RedirectToProduct(code);

        [HttpGet("/u/{code:regex(^[[0-9A-Za-z]]{{8}}$)}")]
        public async Task<IActionResult> GoUniversal(string code, CancellationToken cancellationToken)
        {
            var destination = await shortLinks.ResolveDestinationAsync(code, cancellationToken);
            if (string.IsNullOrWhiteSpace(destination))
                return NotFound();

            return Redirect(AppendTag(destination));
        }

        private IActionResult RedirectToProduct(string code)
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
