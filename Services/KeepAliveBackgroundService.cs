using Affiliate.Options;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    /// <summary>
    /// In-app keep-alive: periodically GETs /health so IIS/shared hosting idle timeout does not sleep the process.
    /// URL is auto-detected (no config) from server addresses, ASPNETCORE_URLS, or the first HTTP request.
    /// </summary>
    public sealed class KeepAliveBackgroundService : BackgroundService
    {
        public const string HttpClientName = "KeepAlive";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServer _server;
        private readonly KeepAliveUrlStore _urlStore;
        private readonly KeepAliveOptions _options;
        private readonly ILogger<KeepAliveBackgroundService> _logger;

        public KeepAliveBackgroundService(
            IHttpClientFactory httpClientFactory,
            IServer server,
            KeepAliveUrlStore urlStore,
            IOptions<KeepAliveOptions> options,
            ILogger<KeepAliveBackgroundService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _server = server;
            _urlStore = urlStore;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Keep-alive ping disabled");
                return;
            }

            var minutes = _options.IntervalMinutes > 0 ? _options.IntervalMinutes : 5;
            var interval = TimeSpan.FromMinutes(minutes);

            _logger.LogInformation("Keep-alive ping started (every {Interval})", interval);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            using var timer = new PeriodicTimer(interval);

            do
            {
                await PingAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task PingAsync(CancellationToken cancellationToken)
        {
            var url = ResolveUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogDebug("Keep-alive skipped — waiting to learn site URL from server or first request");
                return;
            }

            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var response = await client.GetAsync(url, cancellationToken);
                _logger.LogDebug("Keep-alive ping {Url} → {StatusCode}", url, (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Keep-alive ping failed for {Url}", url);
            }
        }

        private string? ResolveUrl()
        {
            var stored = _urlStore.Url;
            if (!string.IsNullOrWhiteSpace(stored))
                return stored;

            foreach (var baseAddr in EnumerateBaseAddresses())
            {
                var url = $"{baseAddr.TrimEnd('/')}/health";
                _urlStore.TrySet(url);
                return url;
            }

            return null;
        }

        private IEnumerable<string> EnumerateBaseAddresses()
        {
            var fromServer = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
            if (fromServer != null)
            {
                foreach (var address in fromServer)
                {
                    if (IsHttpAddress(address))
                        yield return address;
                }
            }

            var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
            if (string.IsNullOrWhiteSpace(urls))
                yield break;

            foreach (var part in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsHttpAddress(part))
                    yield return part;
            }
        }

        private static bool IsHttpAddress(string address) =>
            address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            address.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}
