using Microsoft.AspNetCore.Http;

namespace Affiliate.Services
{
    /// <summary>Remembers the public base URL from the first HTTP request (for self keep-alive pings).</summary>
    public sealed class KeepAliveUrlStore
    {
        private string? _url;
        private readonly object _lock = new();

        public string? Url
        {
            get
            {
                lock (_lock)
                    return _url;
            }
        }

        public void TrySetFromRequest(string scheme, HostString host)
        {
            if (string.IsNullOrWhiteSpace(host.Value))
                return;

            var candidate = $"{scheme}://{host.Value}/health";
            lock (_lock)
            {
                _url ??= candidate;
            }
        }

        public void TrySet(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            lock (_lock)
            {
                _url ??= url.Trim();
            }
        }
    }
}
