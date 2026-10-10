using System.Diagnostics;
using Affiliate.Data;
using Affiliate.Models;
using Affiliate.Noon.Models;
using Affiliate.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;

        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

        public async Task<IActionResult> Index([FromServices] AffiliateDbContext db)
        {
            var since = DateTime.UtcNow.AddHours(-1);
            return View(new HomeDashboardViewModel
            {
                AmazonProducts = await db.Products.CountAsync(),
                AmazonAvailable = await db.Products.CountAsync(p => p.IsAvailable),
                AmazonUrls = await db.ScraperUrls.CountAsync(s => s.IsEnabled),
                NoonProducts = await db.NoonProducts.CountAsync(),
                NoonAvailable = await db.NoonProducts.CountAsync(p => p.IsAvailable),
                NoonBrands = await db.NoonBrandUrls.CountAsync(b => b.IsEnabled),
                NoonRequestsLastHour = await db.NoonRequestLogs.CountAsync(l => l.RequestedAt >= since),
                NoonFailedLastHour = await db.NoonRequestLogs.CountAsync(l => l.RequestedAt >= since && l.Outcome != NoonRequestOutcome.Success),
                Links = await db.ProductLinks.CountAsync()
            });
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
