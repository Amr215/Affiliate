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

    public interface IIspProxyService
    {
        /// <summary>Configured proxy endpoint (or direct when disabled).</summary>
        IspProxyEndpoint GetEndpoint();

        /// <summary>Retries on the same connection for a dropped request before failing.</summary>
        int TransportRetriesPerIp { get; }

        HttpClient CreateClient(IspProxyEndpoint endpoint);
    }

    public sealed class IspProxyService : IIspProxyService
    {
        private readonly IspProxyOptions _options;
        private readonly ILogger<IspProxyService> _logger;

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

            var port = PickRandomPort();
            var endpoint = new IspProxyEndpoint(
                true, _options.Host.Trim(), port, _options.Username, _options.Password);
            _logger.LogInformation("ISP proxy selected {Endpoint}", endpoint.Describe());
            return endpoint;
        }

        private int PickRandomPort()
        {
            var min = _options.PortMin > 0 ? _options.PortMin : 8001;
            var max = _options.PortMax >= min ? _options.PortMax : min;
            return Random.Shared.Next(min, max + 1);
        }

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
    }
}
