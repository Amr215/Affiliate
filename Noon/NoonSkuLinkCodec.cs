using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Affiliate.Options;
using Microsoft.Extensions.Options;

namespace Affiliate.Noon
{
    /// <summary>
    /// Reversible Noon SKU ↔ short opaque link code (11 base62 chars).
    /// Same idea as <see cref="Services.AsinLinkCodec"/>: product links are not stored in the DB.
    /// Public form: <c>{PublishLinkBaseUrl}/n/{code}</c> (length 11; general Noon links stay length 8).
    /// </summary>
    public sealed class NoonSkuLinkCodec
    {
        private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        /// <summary>62^11 &gt; 36^12, so every padded SKU fits.</summary>
        public const int CodeLength = 11;

        /// <summary>SKU is left-padded with '0' to this width for encoding (6–12 char SKUs).</summary>
        private const int PaddedLength = 12;

        private static readonly ulong Modulus = Pow(36, PaddedLength);

        private readonly ulong _mul;
        private readonly ulong _mulInv;
        private readonly ulong _add;
        private readonly string _baseUrl;

        public NoonSkuLinkCodec(IOptions<TelegramOptions> options)
        {
            var secret = options.Value.LinkCodeSecret?.Trim();
            if (string.IsNullOrEmpty(secret))
                throw new InvalidOperationException(
                    "Telegram:LinkCodeSecret is required to encode/decode Noon short link codes.");

            // Distinct from Amazon ASIN params so the two codecs never share keys.
            DeriveParams("noon-sku:" + secret, out _mul, out _mulInv, out _add);

            _baseUrl = (options.Value.PublishLinkBaseUrl ?? "https://el-sindbad.com").Trim().TrimEnd('/');
            if (_baseUrl.Length == 0)
                _baseUrl = "https://el-sindbad.com";
        }

        public string BuildPublicUrl(string sku) => $"{_baseUrl}/n/{Encode(sku)}";

        public string Encode(string sku)
        {
            sku = NormalizeSku(sku);
            if (!IsValidSku(sku))
                throw new ArgumentException("Invalid Noon SKU.", nameof(sku));

            var padded = sku.PadLeft(PaddedLength, '0');
            var n = ParseBase36(padded);
            var mixed = Mix(n);
            return ToBase62(mixed, CodeLength);
        }

        public bool TryDecode(string? code, out string sku)
        {
            sku = string.Empty;
            code = (code ?? string.Empty).Trim();
            if (code.Length != CodeLength)
                return false;

            if (!TryParseBase62(code, out var mixed) || mixed >= Modulus)
                return false;

            var n = Unmix(mixed);
            var padded = ToBase36(n, PaddedLength);
            sku = padded.TrimStart('0');
            return IsValidSku(sku);
        }

        private ulong Mix(ulong n) => (MulMod(n, _mul, Modulus) + _add) % Modulus;

        private ulong Unmix(ulong y)
        {
            var t = (y + Modulus - _add) % Modulus;
            return MulMod(t, _mulInv, Modulus);
        }

        private static void DeriveParams(string secret, out ulong mul, out ulong mulInv, out ulong add)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
            add = ReadUInt64(hash, 0) % Modulus;

            // Modulus = 36^12 = 2^24 * 3^12 — multiplier must stay odd and not divisible by 3.
            var raw = ReadUInt64(hash, 8) % Modulus;
            mul = raw | 1UL;
            while (mul == 0 || mul % 3 == 0)
                mul = ((mul + 2) % Modulus) | 1UL;

            mulInv = ModInverse(mul, Modulus);
        }

        private static ulong ReadUInt64(byte[] bytes, int offset) =>
            BitConverter.ToUInt64(bytes.AsSpan(offset, 8));

        private static ulong MulMod(ulong a, ulong b, ulong mod) =>
            (ulong)(((BigInteger)a * b) % mod);

        private static ulong ModInverse(ulong a, ulong m)
        {
            long oldR = (long)a, r = (long)m;
            long oldS = 1, s = 0;
            while (r != 0)
            {
                var q = oldR / r;
                (oldR, r) = (r, oldR - q * r);
                (oldS, s) = (s, oldS - q * s);
            }

            if (oldR != 1)
                throw new InvalidOperationException("Noon link code multiplier is not invertible.");

            if (oldS < 0)
                oldS += (long)m;
            return (ulong)oldS;
        }

        private static ulong Pow(ulong b, int e)
        {
            ulong r = 1;
            for (var i = 0; i < e; i++)
                r *= b;
            return r;
        }

        internal static string NormalizeSku(string sku) =>
            (sku ?? string.Empty).Trim().ToUpperInvariant();

        /// <summary>Noon product SKUs: N + 5–11 alphanumerics (total 6–12).</summary>
        internal static bool IsValidSku(string sku) =>
            sku.Length is >= 6 and <= 12
            && sku[0] == 'N'
            && sku.Skip(1).All(c => char.IsAsciiLetterOrDigit(c));

        private static ulong ParseBase36(string value)
        {
            ulong n = 0;
            foreach (var c in value)
            {
                var d = DigitValue(c);
                if (d < 0 || d >= 36)
                    throw new ArgumentException("SKU has non-base36 characters.");
                n = n * 36 + (ulong)d;
            }
            return n;
        }

        private static string ToBase36(ulong n, int length)
        {
            Span<char> buf = stackalloc char[length];
            for (var i = length - 1; i >= 0; i--)
            {
                var d = (int)(n % 36);
                buf[i] = (char)(d < 10 ? '0' + d : 'A' + (d - 10));
                n /= 36;
            }
            return new string(buf);
        }

        private static string ToBase62(ulong n, int length)
        {
            Span<char> buf = stackalloc char[length];
            for (var i = length - 1; i >= 0; i--)
            {
                buf[i] = Alphabet[(int)(n % 62)];
                n /= 62;
            }
            return new string(buf);
        }

        private static bool TryParseBase62(string value, out ulong n)
        {
            n = 0;
            foreach (var c in value)
            {
                var d = Alphabet.IndexOf(c);
                if (d < 0)
                    return false;
                n = n * 62 + (ulong)d;
            }
            return true;
        }

        private static int DigitValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'Z' => c - 'A' + 10,
            >= 'a' and <= 'z' => c - 'a' + 10,
            _ => -1
        };
    }
}
