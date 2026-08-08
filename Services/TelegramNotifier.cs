using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Web;
using Affiliate.Models;
using Affiliate.Options;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    public interface ITelegramNotifier
    {
        /// <summary>Returns true when the price-drop message was delivered to the main chat.</summary>
        Task<bool> NotifyDropAsync(ProductDropAlert alert, CancellationToken cancellationToken = default);

        /// <summary>Sends a plain test message to verify BotToken/ChatId.</summary>
        Task<(bool Success, string Detail)> SendTestMessageAsync(CancellationToken cancellationToken = default);
    }

    public sealed class ProductDropAlert
    {
        public required Product Product { get; init; }

        /// <summary>Percent drop of the current price versus the previous recorded price.</summary>
        public required decimal DropPercent { get; init; }

        /// <summary>Previous recorded price the drop is measured against.</summary>
        public decimal? BaselinePrice { get; init; }

        public decimal? CurrentPrice { get; init; }

        /// <summary>
        /// Price history for the alert (oldest → newest). When truncated, only first 5 + last 5
        /// are included and <see cref="HistoryTruncated"/> is true.
        /// </summary>
        public IReadOnlyList<PriceHistoryPoint> History { get; set; } = [];

        /// <summary>Average of all recorded prices (not only the truncated History window).</summary>
        public decimal? AveragePrice { get; set; }

        /// <summary>True when middle history points were omitted from <see cref="History"/>.</summary>
        public bool HistoryTruncated { get; set; }
    }

    /// <summary>A price-history data point rendered inside a drop alert (label optional).</summary>
    public sealed record PriceHistoryPoint(string Label, decimal? Price, DateTime? CheckedAt);

    public sealed class TelegramNotifier : ITelegramNotifier
    {
        public const string HttpClientName = "TelegramBot";

        private readonly TelegramOptions _options;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TelegramNotifier> _logger;

        public TelegramNotifier(
            IOptions<TelegramOptions> options,
            IHttpClientFactory httpClientFactory,
            ILogger<TelegramNotifier> logger)
        {
            _options = options.Value;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<bool> NotifyDropAsync(ProductDropAlert alert, CancellationToken cancellationToken = default)
        {
            var text = BuildDropHtml(alert);

            if (!_options.Enabled)
            {
                _logger.LogInformation(
                    "Telegram disabled — drop alert not sent: {Asin} {Percent}%",
                    alert.Product.Asin, alert.DropPercent);
                return false;
            }

            if (!IsConfigured(out var detail))
            {
                _logger.LogWarning("Telegram not configured ({Detail}); skipping drop alert", detail);
                return false;
            }

            // Always publish to the main "all drops" group.
            var sentMain = await SendMessageAsync(_options.ChatId.Trim(), text, cancellationToken);

            // Also publish to the matching drop-percentage tier group (when configured).
            var tierChatId = ResolveTierChatId(alert.DropPercent);
            if (!string.IsNullOrWhiteSpace(tierChatId))
            {
                var sentTier = await SendMessageAsync(tierChatId.Trim(), text, cancellationToken);
                if (!sentTier)
                    _logger.LogWarning(
                        "Telegram tier alert failed for {Asin} ({Percent}% → chat {ChatId})",
                        alert.Product.Asin, alert.DropPercent, tierChatId);
            }

            if (sentMain)
                _logger.LogInformation(
                    "Telegram drop alert sent for {Asin} ({Percent}%)",
                    alert.Product.Asin, alert.DropPercent);

            return sentMain;
        }

        public async Task<(bool Success, string Detail)> SendTestMessageAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConfigured(out var detail))
                return (false, detail);

            var text =
                $"✅ <b>Affiliate</b> Telegram is connected.\n" +
                $"Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";

            var sent = await SendMessageAsync(_options.ChatId.Trim(), text, cancellationToken);
            return sent
                ? (true, "Test message sent successfully.")
                : (false, "Telegram API rejected the request. Check logs and BotToken/ChatId.");
        }

        private bool IsConfigured(out string detail)
        {
            if (string.IsNullOrWhiteSpace(_options.BotToken))
            {
                detail = "Telegram:BotToken is empty";
                return false;
            }

            if (string.IsNullOrWhiteSpace(_options.ChatId))
            {
                detail = "Telegram:ChatId is empty";
                return false;
            }

            detail = string.Empty;
            return true;
        }

        /// <summary>
        /// Maps drop % to a tier chat. Boundaries use inclusive lower / exclusive upper
        /// except the top band: [1,20), [20,40), [40,60), [60,80), [80,100].
        /// </summary>
        private string? ResolveTierChatId(decimal dropPercent)
        {
            if (dropPercent >= 80m)
                return NullIfBlank(_options.ChatId80To100);
            if (dropPercent >= 60m)
                return NullIfBlank(_options.ChatId60To80);
            if (dropPercent >= 40m)
                return NullIfBlank(_options.ChatId40To60);
            if (dropPercent >= 20m)
                return NullIfBlank(_options.ChatId20To40);
            if (dropPercent >= 1m)
                return NullIfBlank(_options.ChatId1To20);

            return null;
        }

        private static string? NullIfBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;

        private async Task<bool> SendMessageAsync(string chatId, string text, CancellationToken cancellationToken)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                var url = $"https://api.telegram.org/bot{_options.BotToken.Trim()}/sendMessage";

                var payload = new TelegramSendMessageRequest
                {
                    ChatId = chatId,
                    Text = text,
                    ParseMode = "HTML",
                    DisableWebPagePreview = false
                };

                using var response = await client.PostAsJsonAsync(url, payload, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "Telegram sendMessage failed (chat {ChatId}): {StatusCode} {Body}",
                        chatId, (int)response.StatusCode, body);
                    return false;
                }

                var result = System.Text.Json.JsonSerializer.Deserialize<TelegramApiResponse>(body);
                if (result is not { Ok: true })
                {
                    _logger.LogError("Telegram sendMessage returned ok=false (chat {ChatId}): {Body}", chatId, body);
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Telegram sendMessage threw (chat {ChatId})", chatId);
                return false;
            }
        }

        private static string BuildDropHtml(ProductDropAlert alert)
        {
            var product = alert.Product;
            var name = Html(product.Name);
            var asin = Html(product.Asin ?? "");
            var currency = Html(product.Currency ?? "");
            var sb = new StringBuilder();

            sb.AppendLine($"🔻 <b>انخفاض السعر {alert.DropPercent:0.#}%</b>");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(product.Asin)
                ? $"<b>{name}</b>"
                : $"<a href=\"{Html($"https://www.amazon.eg/dp/{product.Asin.Trim()}?language=ar_AE")}\">{name}</a>");
            sb.AppendLine($"ASIN: <code>{asin}</code>");
            sb.AppendLine($"السعر الحالي: <b>{alert.CurrentPrice} {currency}</b>");

            if (alert.BaselinePrice.HasValue)
                sb.AppendLine($"السعر السابق: {alert.BaselinePrice} {currency}");

            if (alert.AveragePrice.HasValue)
                sb.AppendLine($"متوسط السعر: <b>{alert.AveragePrice:0.##} {currency}</b>");

            if (alert.History.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("📉 <b>سجل الأسعار (بتوقيت UTC):</b>");

                var history = alert.History;
                var showEllipsis = alert.HistoryTruncated && history.Count >= 10;
                var firstCount = showEllipsis ? 5 : history.Count;
                var lastStart = showEllipsis ? history.Count - 5 : history.Count;

                for (var i = 0; i < firstCount; i++)
                    AppendHistoryLine(sb, history[i], currency);

                if (showEllipsis)
                {
                    sb.AppendLine("• …");
                    for (var i = lastStart; i < history.Count; i++)
                        AppendHistoryLine(sb, history[i], currency);
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static void AppendHistoryLine(StringBuilder sb, PriceHistoryPoint point, string currency)
        {
            var price = point.Price.HasValue ? $"{point.Price} {currency}" : "—";
            var date = point.CheckedAt.HasValue ? $"{point.CheckedAt:yyyy-MM-dd HH:mm}" : null;
            if (!string.IsNullOrWhiteSpace(point.Label))
                sb.AppendLine($"• {point.Label}: {price}" + (date is null ? "" : $" — {date}"));
            else
                sb.AppendLine(date is null ? $"• {price}" : $"• {price} — {date}");
        }

        private static string Html(string? value) =>
            HttpUtility.HtmlEncode(value ?? string.Empty);

        private sealed class TelegramSendMessageRequest
        {
            [JsonPropertyName("chat_id")]
            public string ChatId { get; set; } = string.Empty;

            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;

            [JsonPropertyName("parse_mode")]
            public string ParseMode { get; set; } = "HTML";

            [JsonPropertyName("disable_web_page_preview")]
            public bool DisableWebPagePreview { get; set; }
        }

        private sealed class TelegramApiResponse
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; set; }
        }
    }
}
