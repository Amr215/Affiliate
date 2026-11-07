using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Affiliate.Services
{
    public static partial class PriceTrackerSlug
    {
        public static string FromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "product";

            var normalized = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark)
                    continue;

                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                    continue;
                }

                if (ch is ' ' or '-' or '_' or '.' or '/')
                    sb.Append('-');
            }

            var slug = CollapseDashes().Replace(sb.ToString(), "-").Trim('-');
            if (slug.Length > 80)
                slug = slug[..80].TrimEnd('-');
            return string.IsNullOrEmpty(slug) ? "product" : slug;
        }

        public static bool IsValidAsin(string? asin) =>
            !string.IsNullOrWhiteSpace(asin)
            && AsinPattern().IsMatch(asin.Trim());

        [GeneratedRegex("-{2,}")]
        private static partial Regex CollapseDashes();

        [GeneratedRegex("^[A-Za-z0-9]{10}$")]
        private static partial Regex AsinPattern();
    }
}
