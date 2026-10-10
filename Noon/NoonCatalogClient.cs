using System.Diagnostics;
using System.Net;
using Affiliate.Noon.Models;
using Affiliate.Options;
using Affiliate.Services;
using Microsoft.Extensions.Options;

namespace Affiliate.Noon
{
    /// <summary>
    /// Fetches Noon catalog pages through ISP proxies only — never direct, Tor or Google Translate.
    /// </summary>
    public sealed class NoonCatalogClient(
        IIspProxyService ispProxy,
        NoonProxyBlocklist blocklist,
        IOptions<NoonOptions> options,
        IOptions<IspProxyOptions> proxyOptions,
        ILogger<NoonCatalogClient> logger)
    {
        private const string Origin = "https://www.noon.com";
        private const string ApiPrefix = "/_vs/nc/mp-customer-catalog-api/api/v3/u/";
        private const int MaxLoggedBodyLength = 4000;

        private readonly NoonOptions _options = options.Value;

        /// <summary>
        /// Maps a brand link to its website listing URL for <paramref name="page"/>.
        /// Accepts either the website link or the catalog API link.
        /// </summary>
        public string BuildPageUrl(string url, int page)
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
                !uri.Host.EndsWith("noon.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Not a noon.com link: {url}");

            // Keep the query exactly as pasted; only the page parameter is replaced.
            var queryStart = url.IndexOf('?');
            var query = queryStart < 0 ? string.Empty : url[(queryStart + 1)..];
            var fullQuery = string.Join('&', query
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.StartsWith("page=", StringComparison.OrdinalIgnoreCase))
                .Append($"page={page}"));

            // Website links start with the locale segment (/egypt-en/xiaomi/); API links with the API prefix.
            string rest;
            var path = uri.AbsolutePath;
            if (path.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase))
            {
                rest = path[ApiPrefix.Length..];
            }
            else
            {
                var trimmed = path.TrimStart('/');
                var slash = trimmed.IndexOf('/');
                rest = slash < 0 ? string.Empty : trimmed[(slash + 1)..];
            }

            if (string.IsNullOrWhiteSpace(rest.Trim('/')))
                throw new InvalidOperationException($"The link has no brand path: {url}");

            return $"{Origin}/{_options.Locale}/{rest}?{fullQuery}";
        }

        public string BuildProductUrl(string? pdpUrl) =>
            string.IsNullOrWhiteSpace(pdpUrl) ? string.Empty : $"{Origin}/{_options.Locale}{pdpUrl}";

        /// <summary>
        /// Fetches one page, switching proxy on every failed attempt. Every attempt is passed to
        /// <paramref name="log"/>. Returns null when all attempts failed.
        /// </summary>
        public async Task<NoonCatalogResponse?> FetchPageAsync(
            NoonBrandUrl brand, int page, Action<NoonRequestLog> log, CancellationToken ct)
        {
            var pageUrl = BuildPageUrl(brand.Url, page);
            var attempts = Math.Max(1, _options.MaxAttemptsPerPage);

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var endpoint = PickEndpoint();
                var entry = new NoonRequestLog
                {
                    NoonBrandUrlId = brand.Id,
                    Page = page,
                    Attempt = attempt,
                    Proxy = endpoint.Key,
                    RequestedAt = DateTime.UtcNow,
                    RequestUrl = pageUrl
                };

                var response = await SendAsync(endpoint, pageUrl, entry, ct);
                log(entry);

                if (response is not null)
                {
                    blocklist.ReportSuccess(endpoint.Key);
                    return response;
                }

                // A wrong brand slug; retrying on other proxies gets the same 404.
                if (entry.StatusCode == (int)HttpStatusCode.NotFound)
                    throw new InvalidOperationException($"Noon has no page at this link (404): {pageUrl}");

                if (blocklist.ReportFailure(endpoint.Key))
                    logger.LogWarning("Noon: proxy {Proxy} blocked for Noon after repeated failures", endpoint.Key);
            }

            logger.LogWarning("Noon: {Brand} page {Page} failed after {Attempts} attempts", brand.Name, page, attempts);
            return null;
        }

        private IspProxyEndpoint PickEndpoint()
        {
            IspProxyEndpoint endpoint = null!;
            for (var i = 0; i < 10; i++)
            {
                endpoint = ispProxy.GetEndpoint();
                if (!endpoint.UseProxy)
                    throw new InvalidOperationException("ISP proxies are disabled or not loaded; Noon is only scraped through proxies.");
                if (!blocklist.IsBlocked(endpoint.Key))
                    break;
            }

            return endpoint;
        }

        private async Task<NoonCatalogResponse?> SendAsync(
            IspProxyEndpoint endpoint, string pageUrl, NoonRequestLog entry, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.RequestTimeoutSeconds)));
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var client = CreateClient(endpoint);
                using var request = CreateRequest(pageUrl);
                using var response = await client.SendAsync(request, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                entry.StatusCode = (int)response.StatusCode;

                if (!response.IsSuccessStatusCode)
                    return Fail(entry, NoonRequestOutcome.HttpError, $"HTTP {(int)response.StatusCode}", body);

                var parsed = NoonHtmlParser.Parse(body);
                if (parsed is null)
                    return Fail(entry, NoonRequestOutcome.InvalidResponse, "Page has no catalog data", body);

                entry.Outcome = NoonRequestOutcome.Success;
                entry.NbHits = parsed.NbHits;
                entry.NbPages = parsed.NbPages;
                return parsed;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail(entry, NoonRequestOutcome.Timeout, "Request timed out", null);
            }
            catch (HttpRequestException ex)
            {
                return Fail(entry, NoonRequestOutcome.Transport, ex.InnerException?.Message ?? ex.Message, null);
            }
            finally
            {
                entry.DurationMs = (int)stopwatch.ElapsedMilliseconds;
            }
        }

        private static NoonCatalogResponse? Fail(NoonRequestLog entry, NoonRequestOutcome outcome, string error, string? body)
        {
            entry.Outcome = outcome;
            entry.ErrorMessage = error;
            entry.ResponseBody = body is { Length: > MaxLoggedBodyLength } ? body[..MaxLoggedBodyLength] : body;
            return null;
        }

        private HttpClient CreateClient(IspProxyEndpoint endpoint)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(Math.Max(5, proxyOptions.Value.ConnectTimeoutSeconds)),
                // Webshare proxies serve SOCKS5 on the same port as HTTP.
                Proxy = new WebProxy($"socks5://{endpoint.Host}:{endpoint.Port}")
                {
                    Credentials = new NetworkCredential(endpoint.Username, endpoint.Password)
                },
                UseProxy = true
            };

            return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <summary>
        /// A browser page navigation; the catalog API itself refuses the proxy IPs.
        /// The unique visitor cookie makes Akamai skip its shared cache, which serves anonymous pages rendered hours ago with old prices.
        /// </summary>
        private HttpRequestMessage CreateRequest(string pageUrl)
        {
            var h = _options.Headers;
            var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            foreach (var (name, value) in new[]
                     {
                         ("accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8"),
                         ("accept-language", "en-US,en;q=0.9,ar;q=0.8"),
                         ("cache-control", "no-cache"),
                         ("cookie", $"nloc={h.LocaleCookie}; visitor_id={Guid.NewGuid()}"),
                         ("dnt", "1"),
                         ("priority", "u=0, i"),
                         ("sec-ch-ua", h.SecChUa),
                         ("sec-ch-ua-mobile", "?0"),
                         ("sec-ch-ua-platform", "\"Windows\""),
                         ("sec-fetch-dest", "document"),
                         ("sec-fetch-mode", "navigate"),
                         ("sec-fetch-site", "none"),
                         ("sec-fetch-user", "?1"),
                         ("upgrade-insecure-requests", "1"),
                         ("user-agent", h.UserAgent)
                     })
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            return request;
        }
    }
}
