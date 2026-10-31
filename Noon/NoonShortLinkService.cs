using System.Security.Cryptography;
using System.Web;
using Affiliate.Data;
using Affiliate.Noon.Models;
using Affiliate.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Affiliate.Noon
{
    /// <summary>
    /// Creates and resolves DB-backed short links for Noon URLs that are not classic
    /// <c>N…</c> product SKUs (brand / search / deals, and partner / pzsku product pages).
    /// Classic <c>N…</c> products use <see cref="NoonSkuLinkCodec"/> instead (no DB).
    /// Public form: <c>{PublishLinkBaseUrl}/n/{8-char code}</c>.
    /// </summary>
    public sealed class NoonShortLinkService(
        AffiliateDbContext db,
        IOptions<TelegramOptions> options)
    {
        public const int CodeLength = 8;
        private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        public async Task<string?> CreatePublicUrlAsync(string noonUrl, CancellationToken ct = default)
        {
            if (!TryNormalize(noonUrl, out var destination))
                return null;

            // Classic N-SKU products belong to NoonSkuLinkCodec — do not store them here.
            if (NoonPrepareForPublishService.TryResolveSkuAndUrl(destination, out var sku, out _)
                && NoonSkuLinkCodec.IsValidSku(sku))
                return null;

            var existing = await db.NoonShortLinks.AsNoTracking()
                .Where(x => x.DestinationUrl == destination)
                .Select(x => x.Code)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrEmpty(existing))
                return BuildPublicUrl(existing);

            var code = await AllocateCodeAsync(ct);
            db.NoonShortLinks.Add(new NoonShortLink
            {
                Code = code,
                DestinationUrl = destination
            });
            await db.SaveChangesAsync(ct);
            return BuildPublicUrl(code);
        }

        public async Task<string?> ResolveDestinationAsync(string code, CancellationToken ct = default)
        {
            code = (code ?? string.Empty).Trim();
            if (code.Length != CodeLength)
                return null;

            return await db.NoonShortLinks.AsNoTracking()
                .Where(x => x.Code == code)
                .Select(x => x.DestinationUrl)
                .FirstOrDefaultAsync(ct);
        }

        public string BuildPublicUrl(string code)
        {
            var baseUrl = (options.Value.PublishLinkBaseUrl ?? "https://el-sindbad.com").Trim().TrimEnd('/');
            if (baseUrl.Length == 0)
                baseUrl = "https://el-sindbad.com";
            return $"{baseUrl}/n/{code}";
        }

        /// <summary>Accepts noon.com URLs; keeps path/query; drops common tracking params.</summary>
        public static bool TryNormalize(string? raw, out string destination)
        {
            destination = string.Empty;
            raw = raw?.Trim() ?? "";
            if (raw.Length == 0 || !Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                return false;

            if (!uri.Host.EndsWith("noon.com", StringComparison.OrdinalIgnoreCase))
                return false;

            var query = HttpUtility.ParseQueryString(uri.Query);
            foreach (var key in new[] { "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "gclid", "fbclid" })
                query.Remove(key);

            var builder = new UriBuilder(uri)
            {
                Scheme = "https",
                Host = "www.noon.com",
                Port = -1,
                Query = query.ToString() ?? ""
            };
            destination = builder.Uri.AbsoluteUri;
            return true;
        }

        private async Task<string> AllocateCodeAsync(CancellationToken ct)
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var code = RandomCode();
                var taken = await db.NoonShortLinks.AnyAsync(x => x.Code == code, ct);
                if (!taken)
                    return code;
            }

            throw new InvalidOperationException("Could not allocate a unique Noon short-link code.");
        }

        private static string RandomCode()
        {
            Span<char> buf = stackalloc char[CodeLength];
            Span<byte> bytes = stackalloc byte[CodeLength];
            RandomNumberGenerator.Fill(bytes);
            for (var i = 0; i < CodeLength; i++)
                buf[i] = Alphabet[bytes[i] % Alphabet.Length];
            return new string(buf);
        }
    }
}
