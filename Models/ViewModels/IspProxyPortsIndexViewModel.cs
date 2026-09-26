using Affiliate.Services;

namespace Affiliate.ViewModels
{
    public class IspProxyPortsIndexViewModel
    {
        public bool Enabled { get; set; }
        public int ProxyCount { get; set; }
        public int ConsecutiveFailuresBeforeBlock { get; set; }
        public int BlockDurationSeconds { get; set; }
        public int BlockedCount { get; set; }
        public int AvailableCount { get; set; }
        public IReadOnlyList<IspProxyPortStatus> Ports { get; set; } = [];
    }

    /// <summary>Proxies currently barred from the Google Translate route.</summary>
    public class IspProxyGoogleBlockedViewModel
    {
        public int ProxyCount { get; set; }
        public int TranslateFailuresBeforeBlock { get; set; }
        public int TranslateBlockDurationSeconds { get; set; }
        public IReadOnlyList<IspProxyPortStatus> Ports { get; set; } = [];
    }
}
