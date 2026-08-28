using Affiliate.Options;

namespace Affiliate.Services
{
    /// <summary>
    /// Maps Amazon URLs onto Google's translate proxy host
    /// (<c>www.amazon.eg</c> → <c>www-amazon-eg.translate.goog</c>) and back again, so a proxy IP
    /// that Amazon is currently blocking can still reach search results.
    /// </summary>
    public static class GoogleTranslateProxy
    {
        public const string HostSuffix = ".translate.goog";

        /// <summary>
        /// Written into every request log line for a translate-routed call, so the logs view can
        /// tell them apart without a stored column (transport errors log no URL).
        /// </summary>
        public const string LogMarker = "(translate)";

        /// <summary>Google's host encoding: dots become dashes, and existing dashes are doubled.</summary>
        public static string EncodeHost(string host) =>
            host.Replace("-", "--", StringComparison.Ordinal)
                .Replace(".", "-", StringComparison.Ordinal);

        public static string DecodeHost(string encodedHost)
        {
            const char placeholder = '\u0001';
            return encodedHost
                .Replace("--", placeholder.ToString(), StringComparison.Ordinal)
                .Replace('-', '.')
                .Replace(placeholder, '-');
        }

        /// <summary>The <c>_x_tr_*</c> query parameters, without a leading separator.</summary>
        public static string QueryParams(AsinRecheckTranslateOptions options)
        {
            var sl = Fallback(options.SourceLanguage, "auto");
            var tl = Fallback(options.TargetLanguage, "en");
            var hl = Fallback(options.InterfaceLanguage, "en");
            return $"_x_tr_sl={Uri.EscapeDataString(sl)}" +
                   $"&_x_tr_tl={Uri.EscapeDataString(tl)}" +
                   $"&_x_tr_hl={Uri.EscapeDataString(hl)}" +
                   "&_x_tr_pto=wapp";
        }

        public static string Origin(string amazonHost) =>
            $"https://{EncodeHost(amazonHost)}{HostSuffix}";

        /// <summary>Amazon homepage on the translate host, used to warm up cookies.</summary>
        public static string HomeUrl(string domain, AsinRecheckTranslateOptions options) =>
            $"{Origin($"www.amazon.{domain.Trim()}")}/?{QueryParams(options)}";

        public static bool IsTranslateUrl(string? url) =>
            !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            && uri.Host.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Turns a href taken from a translated page back into a plain Amazon URL, dropping the
        /// <c>_x_tr_*</c> parameters. Relative hrefs keep their shape and only lose those parameters.
        /// </summary>
        public static string? ToAmazonUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            var value = url.Trim();

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Host.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var host = DecodeHost(uri.Host[..^HostSuffix.Length]);
                return $"https://{host}{uri.AbsolutePath}{StripTranslateParams(uri.Query)}";
            }

            var queryStart = value.IndexOf('?');
            return queryStart < 0
                ? value
                : value[..queryStart] + StripTranslateParams(value[queryStart..]);
        }

        private static string StripTranslateParams(string query)
        {
            if (string.IsNullOrEmpty(query))
                return string.Empty;

            var kept = query
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => !segment.StartsWith("_x_tr_", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return kept.Count == 0 ? string.Empty : "?" + string.Join('&', kept);
        }

        private static string Fallback(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
