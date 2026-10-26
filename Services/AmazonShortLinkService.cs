using System.Security.Cryptography;
using System.Web;
using Affiliate.Data;
using Affiliate.Models;
using Affiliate.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    /// <summary>Creates and resolves DB-backed short links for non-product Amazon.eg URLs.</summary>
    public sealed class AmazonShortLinkService(
        AffiliateDbContext db,
        IOptions<TelegramOptions> options)
    {
        public const int CodeLength = 8;
        private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        public async Task<string?> CreatePublicUrlAsync(string amazonUrl, CancellationToken ct = default)
        {
            if (!TryNormalize(amazonUrl, out var destination))
                return null;

            var existing = await db.AmazonShortLinks.AsNoTracking()
                .Where(x => x.DestinationUrl == destination)
                .Select(x => x.Code)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrEmpty(existing))
                return BuildPublicUrl(existing);

            var code = await AllocateCodeAsync(ct);
            db.AmazonShortLinks.Add(new AmazonShortLink
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

            return await db.AmazonShortLinks.AsNoTracking()
                .Where(x => x.Code == code)
                .Select(x => x.DestinationUrl)
                .FirstOrDefaultAsync(ct);
        }

        public string BuildPublicUrl(string code)
        {
            var baseUrl = (options.Value.PublishLinkBaseUrl ?? "https://el-sindbad.com").Trim().TrimEnd('/');
            if (baseUrl.Length == 0)
                baseUrl = "https://el-sindbad.com";
            return $"{baseUrl}/u/{code}";
        }

        /// <summary>Accepts amazon.eg URLs; strips tag/ref; keeps path and other query (e.g. discounts-widget).</summary>
        public static bool TryNormalize(string? raw, out string destination)
        {
            destination = string.Empty;
            raw = raw?.Trim() ?? "";
            if (raw.Length == 0 || !Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                return false;

            if (!uri.Host.Contains("amazon.eg", StringComparison.OrdinalIgnoreCase))
                return false;

            var query = HttpUtility.ParseQueryString(uri.Query);
            query.Remove("tag");
            query.Remove("ref_");
            query.Remove("ref");

            var builder = new UriBuilder(uri)
            {
                Scheme = "https",
                Host = "www.amazon.eg",
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
                var taken = await db.AmazonShortLinks.AnyAsync(x => x.Code == code, ct);
                if (!taken)
                    return code;
            }

            throw new InvalidOperationException("Could not allocate a unique short-link code.");
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
