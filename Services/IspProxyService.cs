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

    public sealed record IspProxyPortStatus(
        int Port,
        bool IsBlocked,
        int ConsecutiveFailures,
        DateTimeOffset? BlockedUntilUtc,
        int? RemainingBlockSeconds);

    public interface IIspProxyService
    {
        /// <summary>Configured proxy endpoint (or direct when disabled). Skips temporarily blocked ports.</summary>
        IspProxyEndpoint GetEndpoint();

        /// <summary>Snapshot of every configured port and its block / failure state.</summary>
        IReadOnlyList<IspProxyPortStatus> GetPortStatuses();

        /// <summary>Resets consecutive failure count for the proxy used in a successful operation.</summary>
        void ReportSuccess(IspProxyEndpoint endpoint);

        /// <summary>
        /// Records a failed operation. After enough consecutive failures the port is blocked briefly;
        /// if it was the last unblocked port, all ports are unblocked instead.
        /// </summary>
        void ReportFailure(IspProxyEndpoint endpoint);

        /// <summary>Retries on the same connection for a dropped request before failing.</summary>
        int TransportRetriesPerIp { get; }

        HttpClient CreateClient(IspProxyEndpoint endpoint);
    }

    public sealed class IspProxyService : IIspProxyService
    {
        private readonly IspProxyOptions _options;
        private readonly ILogger<IspProxyService> _logger;
        private readonly object _gate = new();
        private readonly Dictionary<int, PortState> _ports = new();

        public IspProxyService(IOptions<IspProxyOptions> options, ILogger<IspProxyService> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public int TransportRetriesPerIp => Math.Max(0, _options.TransportRetriesPerIp);

        public IspProxyEndpoint GetEndpoint()
        {
            if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Host))
                return new IspProxyEndpoint(false, null, 0, null, null);

            var port = PickRandomAvailablePort();
            var endpoint = new IspProxyEndpoint(
                true, _options.Host.Trim(), port, _options.Username, _options.Password);
            _logger.LogInformation("ISP proxy selected {Endpoint}", endpoint.Describe());
            return endpoint;
        }

        public IReadOnlyList<IspProxyPortStatus> GetPortStatuses()
        {
            var min = _options.PortMin > 0 ? _options.PortMin : 8001;
            var max = _options.PortMax >= min ? _options.PortMax : min;

            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                ExpireBlocks(now);

                var list = new List<IspProxyPortStatus>(max - min + 1);
                for (var port = min; port <= max; port++)
                {
                    _ports.TryGetValue(port, out var state);
                    var blockedUntil = state?.BlockedUntil;
                    var isBlocked = blockedUntil is { } until && until > now;
                    int? remaining = null;
                    if (isBlocked && blockedUntil is { } bu)
                        remaining = Math.Max(0, (int)Math.Ceiling((bu - now).TotalSeconds));

                    list.Add(new IspProxyPortStatus(
                        port,
                        isBlocked,
                        state?.ConsecutiveFailures ?? 0,
                        isBlocked ? blockedUntil : null,
                        remaining));
                }

                return list;
            }
        }

        public void ReportSuccess(IspProxyEndpoint endpoint)
        {
            if (!endpoint.UseProxy)
                return;

            lock (_gate)
            {
                var state = GetOrCreate(endpoint.Port);
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

            var threshold = Math.Max(1, _options.ConsecutiveFailuresBeforeBlock);
            var blockSeconds = Math.Max(1, _options.BlockDurationSeconds);

            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                ExpireBlocks(now);

                var state = GetOrCreate(endpoint.Port);
                state.ConsecutiveFailures++;

                _logger.LogWarning(
                    "ISP proxy {Port} failed ({Failures}/{Threshold} consecutive)",
                    endpoint.Port, state.ConsecutiveFailures, threshold);

                if (state.ConsecutiveFailures < threshold)
                    return;

                var unblockedCount = CountUnblocked(now);
                // This port is still counted as unblocked until we block it.
                if (unblockedCount <= 1)
                {
                    UnblockAllUnlocked();
                    _logger.LogWarning(
                        "ISP proxy {Port} hit {Threshold} consecutive failures as the last unblocked proxy; unblocked all ports",
                        endpoint.Port, threshold);
                    return;
                }

                state.BlockedUntil = now.AddSeconds(blockSeconds);
                state.ConsecutiveFailures = 0;
                _logger.LogWarning(
                    "ISP proxy {Port} blocked for {Seconds}s after {Threshold} consecutive failures ({Remaining} still available)",
                    endpoint.Port, blockSeconds, threshold, unblockedCount - 1);
            }
        }

        private int PickRandomAvailablePort()
        {
            var min = _options.PortMin > 0 ? _options.PortMin : 8001;
            var max = _options.PortMax >= min ? _options.PortMax : min;

            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                ExpireBlocks(now);

                var available = new List<int>(max - min + 1);
                for (var port = min; port <= max; port++)
                {
                    if (!_ports.TryGetValue(port, out var state) || IsAvailable(state, now))
                        available.Add(port);
                }

                if (available.Count == 0)
                {
                    // Safety net if every port is somehow blocked.
                    UnblockAllUnlocked();
                    for (var port = min; port <= max; port++)
                        available.Add(port);

                    _logger.LogWarning(
                        "No ISP proxy ports available; unblocked all ports in {Min}-{Max}",
                        min, max);
                }

                return available[Random.Shared.Next(available.Count)];
            }
        }

        private PortState GetOrCreate(int port)
        {
            if (!_ports.TryGetValue(port, out var state))
            {
                state = new PortState();
                _ports[port] = state;
            }

            return state;
        }

        private int CountUnblocked(DateTimeOffset now)
        {
            var min = _options.PortMin > 0 ? _options.PortMin : 8001;
            var max = _options.PortMax >= min ? _options.PortMax : min;
            var count = 0;
            for (var port = min; port <= max; port++)
            {
                if (!_ports.TryGetValue(port, out var state) || IsAvailable(state, now))
                    count++;
            }

            return count;
        }

        private void ExpireBlocks(DateTimeOffset now)
        {
            foreach (var state in _ports.Values)
            {
                if (state.BlockedUntil is { } until && until <= now)
                {
                    state.BlockedUntil = null;
                    state.ConsecutiveFailures = 0;
                }
            }
        }

        private void UnblockAllUnlocked()
        {
            foreach (var state in _ports.Values)
            {
                state.BlockedUntil = null;
                state.ConsecutiveFailures = 0;
            }
        }

        private static bool IsAvailable(PortState state, DateTimeOffset now) =>
            state.BlockedUntil is null || state.BlockedUntil <= now;

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

        private sealed class PortState
        {
            public int ConsecutiveFailures;
            public DateTimeOffset? BlockedUntil;
        }
    }
}
