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
}
