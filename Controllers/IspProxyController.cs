using Affiliate.Options;
using Affiliate.Services;
using Affiliate.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Affiliate.Controllers
{
    public class IspProxyController : Controller
    {
        private readonly IIspProxyService _ispProxy;
        private readonly IspProxyOptions _options;

        public IspProxyController(IIspProxyService ispProxy, IOptions<IspProxyOptions> options)
        {
            _ispProxy = ispProxy;
            _options = options.Value;
        }

        public IActionResult Index()
        {
            var ports = _ispProxy.GetPortStatuses();
            var blocked = ports.Count(p => p.IsBlocked);

            return View(new IspProxyPortsIndexViewModel
            {
                Enabled = _options.Enabled,
                Host = _options.Host,
                PortMin = _options.PortMin,
                PortMax = _options.PortMax,
                ConsecutiveFailuresBeforeBlock = _options.ConsecutiveFailuresBeforeBlock,
                BlockDurationSeconds = _options.BlockDurationSeconds,
                BlockedCount = blocked,
                AvailableCount = ports.Count - blocked,
                Ports = ports
            });
        }
    }
}
