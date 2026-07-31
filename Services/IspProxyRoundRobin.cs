using System.Collections.Concurrent;
using System.Net;
using Affiliate.Options;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    public sealed record IspProxyEndpoint(bool UseProxy, string? Host, int Port, string? Username, string? Password)
    {
        public string Key => UseProxy ? $"{Host}:{Port}" : "direct";

        public string Describe() =>
            UseProxy ? $"{Host}:{Port}" : "direct (no proxy)";
    }

    public interface IIspProxyRoundRobin
    {
        /// <summary>Next healthy sticky endpoint (skips IPs in cooldown).</summary>
        IspProxyEndpoint Next(IReadOnlyCollection<string>? excludeKeys = null);

        /// <summary>Quarantine an IP after captcha/blank/block — will not be reused until cooldown ends.</summary>
        void MarkBad(IspProxyEndpoint endpoint, string reason);

        /// <summary>
        /// Short cooldown after a dropped connection. Only escalates to the full quarantine once the
        /// same IP fails this way repeatedly.
        /// </summary>
        void MarkTransient(IspProxyEndpoint endpoint, string reason);

        /// <summary>Clears transport failure strikes after a successful fetch on that IP.</summary>
        void MarkHealthy(IspProxyEndpoint endpoint);

        /// <summary>How many distinct proxy ports are configured (1 when direct).</summary>
        int AvailablePortCount { get; }

        /// <summary>How many proxy ports are usable right now (configured minus those in cooldown).</summary>
        int HealthyPortCount { get; }

        /// <summary>Retries allowed on the same IP before rotating away on a dropped connection.</summary>
        int TransportRetriesPerIp { get; }

        HttpClient CreateClient(IspProxyEndpoint endpoint);
    }

    public sealed class IspProxyRoundRobin : IIspProxyRoundRobin
    {
        private readonly IspProxyOptions _options;
        private readonly ILogger<IspProxyRoundRobin> _logger;
        private readonly ConcurrentDictionary<string, DateTime> _badUntil = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _transportStrikes = new(StringComparer.OrdinalIgnoreCase);
        private int _cursor = -1;

        public IspProxyRoundRobin(IOptions<IspProxyOptions> options, ILogger<IspProxyRoundRobin> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public int AvailablePortCount
        {
            get
            {
                if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Host))
                    return 1;
                return Math.Max(1, GetPorts().Length);
            }
        }

        public int HealthyPortCount
        {
            get
            {
                if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Host))
                    return 1;

                var now = DateTime.UtcNow;
                PruneExpired(now);

                return GetPorts().Count(port =>
                    !_badUntil.TryGetValue(MakeEndpoint(port).Key, out var until) || until <= now);
            }
        }

        public IspProxyEndpoint Next(IReadOnlyCollection<string>? excludeKeys = null)
        {
            if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Host))
                return new IspProxyEndpoint(false, null, 0, null, null);

            var ports = GetPorts();
            var now = DateTime.UtcNow;
            PruneExpired(now);

            // Try up to all ports once for a healthy, non-excluded pick.
            for (var attempt = 0; attempt < ports.Length; attempt++)
            {
                var next = Interlocked.Increment(ref _cursor);
                var port = ports[(next & int.MaxValue) % ports.Length];
                var endpoint = MakeEndpoint(port);

                if (excludeKeys is { Count: > 0 } && excludeKeys.Contains(endpoint.Key))
                    continue;

                if (_badUntil.TryGetValue(endpoint.Key, out var until) && until > now)
                    continue;

                _logger.LogInformation("ISP proxy selected {Endpoint}", endpoint.Describe());
                return endpoint;
            }

            // All ports excluded or in cooldown — pick next round-robin anyway but log loudly.
            var fallbackPort = ports[(Interlocked.Increment(ref _cursor) & int.MaxValue) % ports.Length];
            var fallback = MakeEndpoint(fallbackPort);
            _logger.LogWarning(
                "ISP proxy: all ports cooling down or excluded; forcing {Endpoint}",
                fallback.Describe());
            return fallback;
        }

        public void MarkBad(IspProxyEndpoint endpoint, string reason)
        {
            if (!endpoint.UseProxy)
                return;

            var minutes = Math.Max(5, _options.BadIpCooldownoutMinutes);
            var until = DateTime.UtcNow.AddMinutes(minutes);
            _badUntil[endpoint.Key] = until;
            _transportStrikes.TryRemove(endpoint.Key, out _);
            _logger.LogWarning(
                "ISP proxy quarantined {Endpoint} for {Minutes}m — {Reason}",
                endpoint.Describe(), minutes, reason);
        }

        public void MarkTransient(IspProxyEndpoint endpoint, string reason)
        {
            if (!endpoint.UseProxy)
                return;

            var limit = Math.Max(1, _options.TransientStrikesBeforeQuarantine);
            var strikes = _transportStrikes.AddOrUpdate(endpoint.Key, 1, (_, current) => current + 1);

            if (strikes >= limit)
            {
                MarkBad(endpoint, $"{strikes} consecutive transport failures — {reason}");
                return;
            }

            var seconds = Math.Max(10, _options.TransientCooldownSeconds);
            _badUntil[endpoint.Key] = DateTime.UtcNow.AddSeconds(seconds);
            _logger.LogWarning(
                "ISP proxy {Endpoint} paused {Seconds}s (transport failure {Strikes}/{Limit}) — {Reason}",
                endpoint.Describe(), seconds, strikes, limit, reason);
        }

        public void MarkHealthy(IspProxyEndpoint endpoint)
        {
            if (endpoint.UseProxy)
                _transportStrikes.TryRemove(endpoint.Key, out _);
        }

        public int TransportRetriesPerIp => Math.Max(0, _options.TransportRetriesPerIp);

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
                // Sticky proxy sessions go stale; recycle before the peer silently drops them.
                PooledConnectionLifetime = TimeSpan.FromMinutes(4),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
                MaxConnectionsPerServer = 4,
                EnableMultipleHttp2Connections = true
            };

            if (endpoint.UseProxy && !string.IsNullOrWhiteSpace(endpoint.Host))
            {
                var webProxy = new WebProxy(endpoint.Host, endpoint.Port);
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

        private IspProxyEndpoint MakeEndpoint(int port) =>
            new(true, _options.Host!.Trim(), port, _options.Username, _options.Password);

        private void PruneExpired(DateTime now)
        {
            foreach (var kv in _badUntil)
            {
                if (kv.Value <= now)
                    _badUntil.TryRemove(kv.Key, out _);
            }
        }

        private int[] GetPorts()
        {
            var ports = (_options.Ports ?? [])
                .Where(p => p > 0)
                .Distinct()
                .ToArray();

            if (ports.Length > 0)
                return ports;

            return [_options.Port > 0 ? _options.Port : 8001];
        }
    }
}
