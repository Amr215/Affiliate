using System.Net;
using Affiliate.Options;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    public sealed record IspProxyEndpoint(
        bool UseProxy,
        string? Host,
        int Port,
        string? Username,
        string? Password,
        bool RouteThroughTranslate = false)
    {
        public string Key => UseProxy ? $"{Host}:{Port}" : "direct";

        public string Describe() =>
            UseProxy
                ? RouteThroughTranslate
                    ? $"{Host}:{Port} {GoogleTranslateProxy.LogMarker}"
                    : $"{Host}:{Port}"
                : "direct (no proxy)";
    }

    public sealed record IspProxyPortStatus(
        string? Host,
        int Port,
        bool IsBlocked,
        int ConsecutiveFailures,
        DateTimeOffset? BlockedUntilUtc,
        int? RemainingBlockSeconds);

    public interface IIspProxyService
    {
        /// <summary>
        /// Configured proxy endpoint (or direct when disabled). Temporarily blocked proxies are skipped
        /// unless <paramref name="allowBlocked"/> is set, in which case they can still be handed out
        /// with <see cref="IspProxyEndpoint.RouteThroughTranslate"/> so the caller reaches Amazon
        /// through Google Translate rather than leaving the IP idle.
        /// </summary>
        IspProxyEndpoint GetEndpoint(bool allowBlocked = false);

        /// <summary>Snapshot of every configured proxy and its block / failure state.</summary>
        IReadOnlyList<IspProxyPortStatus> GetPortStatuses();

        /// <summary>
        /// Resets consecutive failure count for the proxy used in a successful operation.
        /// Successes on the translate route are ignored: they say nothing about whether Amazon
        /// still blocks the IP directly, so neither the counter nor the block window is touched.
        /// </summary>
        void ReportSuccess(IspProxyEndpoint endpoint);

        /// <summary>
        /// Records a failed operation. After enough consecutive failures the proxy is blocked briefly;
        /// if it was the last unblocked proxy, that proxy is still blocked and half of the other
        /// blocked proxies (shortest remaining block time) are freed instead of unblocking everything.
        /// Failures on the translate route are ignored for the same reason successes are: they
        /// describe Google's mood, not whether Amazon still blocks the IP.
        /// </summary>
        void ReportFailure(IspProxyEndpoint endpoint);

        /// <summary>Retries on the same connection for a dropped request before failing.</summary>
        int TransportRetriesPerIp { get; }

        HttpClient CreateClient(IspProxyEndpoint endpoint);
    }

    public sealed class IspProxyService : IIspProxyService
    {
        private static readonly IspProxyEndpoint Direct = new(false, null, 0, null, null);

        private readonly IspProxyOptions _options;
        private readonly ILogger<IspProxyService> _logger;
        private readonly object _gate = new();
        private readonly List<IspProxyEndpoint> _proxies;
        private readonly Dictionary<string, ProxyState> _states = new();

        public IspProxyService(
            IOptions<IspProxyOptions> options,
            IWebHostEnvironment env,
            ILogger<IspProxyService> logger)
        {
            _options = options.Value;
            _logger = logger;
            _proxies = ParseProxies(ReadProxyLines(env));
        }

        public int TransportRetriesPerIp => Math.Max(0, _options.TransportRetriesPerIp);

        public IspProxyEndpoint GetEndpoint(bool allowBlocked = false)
        {
            if (!_options.Enabled || _proxies.Count == 0)
                return Direct;

            IspProxyEndpoint endpoint;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                ExpireBlocks(now);
                endpoint = allowBlocked ? PickAny(now) : PickAvailable(now);
            }

            _logger.LogInformation("ISP proxy selected {Endpoint}", endpoint.Describe());
            return endpoint;
        }

        public IReadOnlyList<IspProxyPortStatus> GetPortStatuses()
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                ExpireBlocks(now);

                var list = new List<IspProxyPortStatus>(_proxies.Count);
                foreach (var proxy in _proxies)
                {
                    _states.TryGetValue(proxy.Key, out var state);
                    var isBlocked = state?.BlockedUntil is { } until && until > now;
                    int? remaining = isBlocked
                        ? Math.Max(0, (int)Math.Ceiling((state!.BlockedUntil!.Value - now).TotalSeconds))
                        : null;

                    list.Add(new IspProxyPortStatus(
                        proxy.Host,
                        proxy.Port,
                        isBlocked,
                        state?.ConsecutiveFailures ?? 0,
                        isBlocked ? state!.BlockedUntil : null,
                        remaining));
                }

                return list;
            }
        }

        public void ReportSuccess(IspProxyEndpoint endpoint)
        {
            if (!endpoint.UseProxy)
                return;

            if (endpoint.RouteThroughTranslate)
            {
                _logger.LogDebug(
                    "ISP proxy {Port} succeeded through Google Translate; failure counter left unchanged",
                    endpoint.Port);
                return;
            }

            lock (_gate)
            {
                var state = GetOrCreate(endpoint.Key);
                if (state.ConsecutiveFailures > 0)
                {
                    _logger.LogDebug(
                        "ISP proxy {Port} succeeded; clearing {Failures} consecutive failure(s)",
                        endpoint.Port, state.ConsecutiveFailures);
                }

                state.ConsecutiveFailures = 0;
            }
        }

        public void ReportFailure(IspProxyEndpoint endpoint)
        {
            if (!endpoint.UseProxy)
                return;

            if (endpoint.RouteThroughTranslate)
            {
                _logger.LogDebug(
                    "ISP proxy {Port} failed through Google Translate; proxy state left unchanged",
                    endpoint.Port);
                return;
            }

            var threshold = Math.Max(1, _options.ConsecutiveFailuresBeforeBlock);
            var blockSeconds = Math.Max(1, _options.BlockDurationSeconds);

            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                ExpireBlocks(now);

                var state = GetOrCreate(endpoint.Key);
                state.ConsecutiveFailures++;

                _logger.LogWarning(
                    "ISP proxy {Port} failed ({Failures}/{Threshold} consecutive)",
                    endpoint.Port, state.ConsecutiveFailures, threshold);

                if (state.ConsecutiveFailures < threshold)
                    return;

                var unblockedCount = _proxies.Count(p => IsAvailable(p.Key, now));
                // This proxy is still counted as unblocked until we block it.
                state.BlockedUntil = now.AddSeconds(blockSeconds);
                state.ConsecutiveFailures = 0;

                if (unblockedCount <= 1)
                {
                    var freed = UnblockShortestHalfUnlocked(now, excludeKey: endpoint.Key);
                    _logger.LogWarning(
                        "ISP proxy {Port} hit {Threshold} consecutive failures as the last unblocked proxy; blocked it for {Seconds}s and freed {Freed} blocked proxy(ies) with the shortest remaining time",
                        endpoint.Port, threshold, blockSeconds, freed);
                    return;
                }

                _logger.LogWarning(
                    "ISP proxy {Port} blocked for {Seconds}s after {Threshold} consecutive failures ({Remaining} still available)",
                    endpoint.Port, blockSeconds, threshold, unblockedCount - 1);
            }
        }

        private IEnumerable<string> ReadProxyLines(IWebHostEnvironment env)
        {
            if (string.IsNullOrWhiteSpace(_options.ProxiesFile))
                return [];

            var path = Path.Combine(env.WebRootPath, _options.ProxiesFile);
            if (!File.Exists(path))
            {
                _logger.LogWarning("ISP proxy file {Path} not found; no proxies loaded", path);
                return [];
            }

            return File.ReadAllLines(path);
        }

        /// <summary>Parses Webshare <c>host:port:username:password</c> entries; malformed ones are skipped.</summary>
        private List<IspProxyEndpoint> ParseProxies(IEnumerable<string> entries)
        {
            var proxies = new List<IspProxyEndpoint>();
            foreach (var raw in entries)
            {
                var entry = raw.Trim();
                if (entry.Length == 0)
                    continue;

                var parts = entry.Split(':');
                if (parts.Length != 4 || !int.TryParse(parts[1], out var port))
                {
                    _logger.LogWarning("Ignoring malformed ISP proxy entry {Entry}", entry);
                    continue;
                }

                proxies.Add(new IspProxyEndpoint(true, parts[0].Trim(), port, parts[2], parts[3]));
            }

            return proxies;
        }

        /// <summary>
        /// Picks from all proxies, telling the caller whether the proxy is currently blocked
        /// so it can be used through the translate route instead of sitting idle.
        /// </summary>
        private IspProxyEndpoint PickAny(DateTimeOffset now)
        {
            var proxy = _proxies[Random.Shared.Next(_proxies.Count)];
            return proxy with { RouteThroughTranslate = !IsAvailable(proxy.Key, now) };
        }

        private IspProxyEndpoint PickAvailable(DateTimeOffset now)
        {
            var available = _proxies.Where(p => IsAvailable(p.Key, now)).ToList();

            if (available.Count == 0)
            {
                // Safety net if every proxy is somehow blocked.
                UnblockAllUnlocked();
                available = _proxies;

                _logger.LogWarning("No ISP proxies available; unblocked all {Count} proxies", _proxies.Count);
            }

            return available[Random.Shared.Next(available.Count)];
        }

        private ProxyState GetOrCreate(string key)
        {
            if (!_states.TryGetValue(key, out var state))
            {
                state = new ProxyState();
                _states[key] = state;
            }

            return state;
        }

        private void ExpireBlocks(DateTimeOffset now)
        {
            foreach (var state in _states.Values)
            {
                if (state.BlockedUntil is { } until && until <= now)
                    state.Reset();
            }
        }

        private void UnblockAllUnlocked()
        {
            foreach (var state in _states.Values)
                state.Reset();
        }

        /// <summary>
        /// Frees ceil(n/2) of currently blocked proxies (excluding <paramref name="excludeKey"/>),
        /// preferring those whose block expires soonest. Even n → n/2; odd n → (n+1)/2.
        /// </summary>
        private int UnblockShortestHalfUnlocked(DateTimeOffset now, string excludeKey)
        {
            var blocked = _proxies
                .Where(p => p.Key != excludeKey && !IsAvailable(p.Key, now))
                .Select(p => (State: _states[p.Key], Port: p.Port))
                .ToList();

            if (blocked.Count == 0)
                return 0;

            // Integer half that rounds up for odd counts: 1→1, 2→1, 3→2, 4→2, 5→3.
            var freeCount = (blocked.Count + 1) / 2;

            foreach (var (state, _) in blocked
                         .OrderBy(b => b.State.BlockedUntil)
                         .ThenBy(b => b.Port)
                         .Take(freeCount))
            {
                state.Reset();
            }

            return freeCount;
        }

        private bool IsAvailable(string key, DateTimeOffset now) =>
            !_states.TryGetValue(key, out var state)
            || state.BlockedUntil is null
            || state.BlockedUntil <= now;

        public HttpClient CreateClient(IspProxyEndpoint endpoint)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 8,
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                UseProxy = false,
                Proxy = null,
                ConnectTimeout = TimeSpan.FromSeconds(Math.Max(5, _options.ConnectTimeoutSeconds)),
                PooledConnectionLifetime = TimeSpan.FromMinutes(4),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
                MaxConnectionsPerServer = 4,
                EnableMultipleHttp2Connections = true
            };

            if (endpoint.UseProxy && !string.IsNullOrWhiteSpace(endpoint.Host))
            {
                // Webshare datacenter proxies serve SOCKS5 on the same port as HTTP.
                var webProxy = new WebProxy($"socks5://{endpoint.Host}:{endpoint.Port}");
                if (!string.IsNullOrWhiteSpace(endpoint.Username))
                {
                    webProxy.Credentials = new NetworkCredential(
                        endpoint.Username, endpoint.Password ?? string.Empty);
                }

                handler.Proxy = webProxy;
                handler.UseProxy = true;
            }

            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(15, _options.RequestTimeoutSeconds))
            };
            AmazonBrowserProfile.ApplyDefaultClientHeaders(client);
            return client;
        }

        private sealed class ProxyState
        {
            public int ConsecutiveFailures;
            public DateTimeOffset? BlockedUntil;

            public void Reset()
            {
                ConsecutiveFailures = 0;
                BlockedUntil = null;
            }
        }
    }
}
