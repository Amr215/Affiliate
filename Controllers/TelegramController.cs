using Affiliate.Options;
using Affiliate.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Affiliate.Controllers
{
    public class TelegramController : Controller
    {
        private readonly ITelegramNotifier _telegram;
        private readonly TelegramOptions _options;

        public TelegramController(
            ITelegramNotifier telegram,
            IOptions<TelegramOptions> options)
        {
            _telegram = telegram;
            _options = options.Value;
        }

        public IActionResult Index()
        {
            ViewBag.Enabled = _options.Enabled;
            ViewBag.HasBotToken = !string.IsNullOrWhiteSpace(_options.BotToken);
            ViewBag.HasChatId = !string.IsNullOrWhiteSpace(_options.PrimaryChatId);
            ViewBag.ChatIdPreview = Mask(_options.PrimaryChatId);
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Test(CancellationToken cancellationToken)
        {
            var (success, detail) = await _telegram.SendTestMessageAsync(cancellationToken);
            TempData["StatusMessage"] = detail;
            TempData["StatusOk"] = success;
            return RedirectToAction(nameof(Index));
        }

        private static string Mask(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "(empty)";
            if (value.Length <= 4)
                return "****";
            return value[..2] + new string('*', Math.Min(8, value.Length - 4)) + value[^2..];
        }
    }
}
