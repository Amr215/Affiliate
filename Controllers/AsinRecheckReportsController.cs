using Affiliate.Services;
using Affiliate.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Affiliate.Controllers
{
    public class AsinRecheckReportsController : Controller
    {
        private static readonly TimeSpan ReportWindow = TimeSpan.FromMinutes(10);

        private readonly IAsinRecheckPollCache _pollCache;

        public AsinRecheckReportsController(IAsinRecheckPollCache pollCache)
        {
            _pollCache = pollCache;
        }

        public IActionResult Index()
        {
            var polls = _pollCache.GetRecent(ReportWindow);
            return View(new AsinRecheckPollReportViewModel
            {
                Window = ReportWindow,
                Polls = polls
            });
        }
    }
}
