using Affiliate.Options;
using Affiliate.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Affiliate.Controllers
{
    /// <summary>
    /// Public Amazon redirect: <c>/{code}</c> → amazon.eg with the configured Associates tag.
    /// The path uses a short opaque code (not the ASIN) so channel posts do not leak product ids.
    /// Constrained to 9 base62 chars so it does not steal controller routes.
    /// </summary>
    [AllowAnonymous]
    public class GoController(
        IOptions<TelegramOptions> options,
        AsinLinkCodec codec) : Controller
    {
        [HttpGet("/{code:regex(^[[0-9A-Za-z]]{{9}}$)}")]
        public IActionResult Go(string code) => RedirectToAmazon(code);

        /// <summary>Keeps older <c>/go/…</c> channel links working.</summary>
        [HttpGet("/go/{code:regex(^[[0-9A-Za-z]]{{9}}$)}")]
        public IActionResult GoLegacy(string code) => RedirectToAmazon(code);

        private IActionResult RedirectToAmazon(string code)
        {
            if (!codec.TryDecode(code, out var asin))
                return NotFound();

            var tag = options.Value.AffiliateTag?.Trim();
            var destination = string.IsNullOrEmpty(tag)
                ? $"https://www.amazon.eg/dp/{asin}"
                : $"https://www.amazon.eg/dp/{asin}?tag={Uri.EscapeDataString(tag)}";

            return Redirect(destination);
        }
    }
}
