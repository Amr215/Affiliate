namespace Affiliate.Services
{
    /// <summary>Chrome-like request headers for Amazon page fetches (direct PC / ISP proxy).</summary>
    public static class AmazonBrowserProfile
    {
        private static readonly BrowserIdentity[] Identities =
        [
            new(
                UserAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                SecChUa: "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"",
                SecChUaFull:
                "\"Google Chrome\";v=\"131.0.6778.86\", \"Chromium\";v=\"131.0.6778.86\", \"Not_A Brand\";v=\"10.0.2.3\""),
            new(
                UserAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
                SecChUa: "\"Chromium\";v=\"130\", \"Google Chrome\";v=\"130\", \"Not?A_Brand\";v=\"99\"",
                SecChUaFull:
                "\"Chromium\";v=\"130.0.6723.117\", \"Google Chrome\";v=\"130.0.6723.117\", \"Not?A_Brand\";v=\"10.0.1.4\""),
            new(
                UserAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36",
                SecChUa: "\"Not A(Brand\";v=\"8\", \"Chromium\";v=\"132\", \"Google Chrome\";v=\"132\"",
                SecChUaFull:
                "\"Not A(Brand\";v=\"10.0.0.4\", \"Chromium\";v=\"132.0.6834.83\", \"Google Chrome\";v=\"132.0.6834.83\""),
        ];

        public const string Accept =
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7";

        public const string AcceptLanguage = "en-AE,en-US;q=0.9,en;q=0.8,ar-EG;q=0.7,ar;q=0.6";

        public static int BeforeFirstRequestDelayMs() => Random.Shared.Next(800, 2200);

        public static int NextPageDelayMs() => Random.Shared.Next(3000, 7000);

        public static int AfterIpSwitchDelayMs() => Random.Shared.Next(1500, 4000);

        /// <summary>Backoff before retrying the same IP after a dropped connection.</summary>
        public static int TransportRetryDelayMs(int attempt) =>
            Random.Shared.Next(700, 1600) * Math.Max(1, attempt);

        /// <summary>Pause between individual ASIN product-page fetches (~10 req/min friendly).</summary>
        public static int BetweenAsinDelayMs() => Random.Shared.Next(4000, 8000);

        /// <summary>Spreads parallel batch starts so all proxy IPs don't fire in the same instant.</summary>
        public static int BatchStaggerMs(int slot) =>
            slot * 250 + Random.Shared.Next(0, 250);

        /// <summary>
        /// Identity headers are applied once per <see cref="HttpClient"/> so that every request on the
        /// same cookie jar keeps one consistent browser fingerprint.
        /// </summary>
        public static void ApplyDefaultClientHeaders(HttpClient client)
        {
            var id = PickIdentity();
            client.DefaultRequestHeaders.Clear();
            ApplyIdentity(client.DefaultRequestHeaders, id);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", Accept);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "max-age=0");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-User", "?1");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "none");
            client.DefaultRequestHeaders.TryAddWithoutValidation("DNT", "1");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Priority", "u=0, i");
        }

        public static void ApplyNavigationHeaders(HttpRequestMessage request, string pageUrl, string? referer)
        {
            request.Version = new Version(2, 0);
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

            request.Headers.TryAddWithoutValidation("Accept", Accept);
            request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
            request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
            request.Headers.TryAddWithoutValidation("Priority", "u=0, i");

            if (!string.IsNullOrWhiteSpace(referer))
            {
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
                request.Headers.TryAddWithoutValidation("Referer", referer);
                return;
            }

            if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            {
                request.Headers.TryAddWithoutValidation("Referer", $"{uri.Scheme}://{uri.Host}/");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            }
            else
            {
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
            }
        }

        private static void ApplyIdentity(System.Net.Http.Headers.HttpRequestHeaders headers, BrowserIdentity id)
        {
            headers.TryAddWithoutValidation("User-Agent", id.UserAgent);
            headers.TryAddWithoutValidation("sec-ch-ua", id.SecChUa);
            headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
            headers.TryAddWithoutValidation("sec-ch-ua-platform-version", "\"15.0.0\"");
            headers.TryAddWithoutValidation("sec-ch-ua-full-version-list", id.SecChUaFull);
        }

        private static BrowserIdentity PickIdentity() =>
            Identities[Random.Shared.Next(Identities.Length)];

        private sealed record BrowserIdentity(string UserAgent, string SecChUa, string SecChUaFull);
    }
}
