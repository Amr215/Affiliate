using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Affiliate.Options;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    /// <summary>
    /// Reversible ASIN ↔ short opaque link code (9 base62 chars).
    /// Uses a secret multiplicative cipher so published short URLs do not expose the ASIN.
    /// </summary>
    public sealed class AsinLinkCodec
    {
        /// <summary>URL-safe base62 alphabet (0-9, A-Z, a-z).</summary>
        private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        /// <summary>Shortest length that can hold any 10-char base36 ASIN (36^10 &lt; 62^9).</summary>
        public const int CodeLength = 9;

        private const int AsinLength = 10;

        /// <summary>36^10 — full ASIN space as an integer.</summary>
        private static readonly ulong Modulus = Pow(36, AsinLength);

        private readonly ulong _mul;
        private readonly ulong _mulInv;
        private readonly ulong _add;

        public AsinLinkCodec(IOptions<TelegramOptions> options)
        {
            var secret = options.Value.LinkCodeSecret?.Trim();
            if (string.IsNullOrEmpty(secret))
                throw new InvalidOperationException(
                    "Telegram:LinkCodeSecret is required to encode/decode short link codes.");

            DeriveParams(secret, out _mul, out _mulInv, out _add);
        }

        public string Encode(string asin)
        {
            asin = NormalizeAsin(asin);
            if (!IsValidAsin(asin))
                throw new ArgumentException("Invalid ASIN.", nameof(asin));

            var n = ParseBase36(asin);
            var mixed = Mix(n);
            return ToBase62(mixed, CodeLength);
        }

        public bool TryDecode(string? code, out string asin)
        {
            asin = string.Empty;
            code = (code ?? string.Empty).Trim();
            if (code.Length != CodeLength)
                return false;

            if (!TryParseBase62(code, out var mixed) || mixed >= Modulus)
                return false;

            var n = Unmix(mixed);
            asin = ToBase36(n, AsinLength);
            return IsValidAsin(asin);
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

            // Multiplier must be coprime to Modulus (2^10 * 3^10): odd and not divisible by 3.
            var raw = ReadUInt64(hash, 8) % Modulus;
            mul = raw | 1UL;
            while (mul == 0 || mul % 3 == 0)
                mul = ((mul + 2) % Modulus) | 1UL;

            mulInv = ModInverse(mul, Modulus);
        }

        private static ulong ReadUInt64(byte[] bytes, int offset) =>
            BitConverter.ToUInt64(bytes.AsSpan(offset, 8));

        private static ulong MulMod(ulong a, ulong b, ulong mod)
        {
            return (ulong)(((BigInteger)a * b) % mod);
        }

        private static ulong ModInverse(ulong a, ulong m)
        {
            // Extended Euclidean for ulong modulus.
            long oldR = (long)a, r = (long)m;
            long oldS = 1, s = 0;
            while (r != 0)
            {
                var q = oldR / r;
                (oldR, r) = (r, oldR - q * r);
                (oldS, s) = (s, oldS - q * s);
            }

            if (oldR != 1)
                throw new InvalidOperationException("Link code multiplier is not invertible.");

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

        internal static string NormalizeAsin(string asin) =>
            (asin ?? string.Empty).Trim().ToUpperInvariant();

        internal static bool IsValidAsin(string asin) =>
            asin.Length == AsinLength && asin.All(c => char.IsAsciiLetterOrDigit(c));

        private static ulong ParseBase36(string value)
        {
            ulong n = 0;
            foreach (var c in value)
            {
                var d = DigitValue(c);
                if (d < 0 || d >= 36)
                    throw new ArgumentException("ASIN has non-base36 characters.");
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
