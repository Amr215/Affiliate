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

            // Publish only to the matching drop-percentage tier group.
            var tierChatId = ResolveTierChatId(alert.DropPercent);
            if (string.IsNullOrWhiteSpace(tierChatId))
            {
                _logger.LogInformation(
                    "No Telegram chat configured for drop tier — alert not sent: {Asin} {Percent}%",
                    alert.Product.Asin, alert.DropPercent);
                return false;
            }

            var chatId = tierChatId.Trim();
            var productUrl = BuildProductUrl(alert.Product.Asin);
            var replyMarkup = BuildAlertReplyMarkupJson(alert.Product.Name, productUrl);
            bool sent;

            // Mega deals need a colored card image — Telegram text messages cannot set a background.
            if (alert.DropPercent > MegaDealDropPercent)
            {
                var productImageBytes = await TryDownloadProductImageAsync(alert.Product.ImageUrl, cancellationToken);
                var png = MegaDealCardImage.Render(alert, productImageBytes);
                var caption = BuildMegaDealCaption(alert);
                sent = await SendPhotoAsync(chatId, png, caption, replyMarkup, cancellationToken);
            }
            else
            {
                sent = await SendMessageAsync(chatId, BuildDropHtml(alert), replyMarkup, cancellationToken);
            }

            if (sent)
                _logger.LogInformation(
                    "Telegram drop alert sent for {Asin} ({Percent}% → chat {ChatId})",
                    alert.Product.Asin, alert.DropPercent, tierChatId);
            else
                _logger.LogWarning(
                    "Telegram drop alert failed for {Asin} ({Percent}% → chat {ChatId})",
                    alert.Product.Asin, alert.DropPercent, tierChatId);

            return sent;
        }

        public async Task<(bool Success, string Detail)> SendTestMessageAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConfigured(out var detail))
                return (false, detail);

            var text =
                $"✅ <b>Affiliate</b> Telegram is connected.\n" +
                $"Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";

            var sent = await SendMessageAsync(_options.PrimaryChatId!.Trim(), text, replyMarkupJson: null, cancellationToken);
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

            if (string.IsNullOrWhiteSpace(_options.PrimaryChatId))
            {
                detail = "No Telegram tier chat id is configured";
                return false;
            }

            detail = string.Empty;
            return true;
        }

        /// <summary>
        /// Maps drop % to a tier chat. Boundaries use inclusive lower / exclusive upper
        /// except the top band: [3,10), [10,20), [20,40), [40,60), [60,80), [80,100].
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
            if (dropPercent >= 10m)
                return NullIfBlank(_options.ChatId10To20);
            if (dropPercent >= 3m)
                return NullIfBlank(_options.ChatId3To10);

            return null;
        }

        private static string? NullIfBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;

        private async Task<bool> SendMessageAsync(
            string chatId,
            string text,
            string? replyMarkupJson,
            CancellationToken cancellationToken)
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
                    DisableWebPagePreview = false,
                    ReplyMarkup = ParseReplyMarkup(replyMarkupJson)
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

        private async Task<bool> SendPhotoAsync(
            string chatId,
            byte[] pngBytes,
            string? captionHtml,
            string? replyMarkupJson,
            CancellationToken cancellationToken)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                var url = $"https://api.telegram.org/bot{_options.BotToken.Trim()}/sendPhoto";

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(chatId), "chat_id");

                var photoContent = new ByteArrayContent(pngBytes);
                photoContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                form.Add(photoContent, "photo", "mega-deal.png");

                if (!string.IsNullOrWhiteSpace(captionHtml))
                {
                    form.Add(new StringContent(captionHtml), "caption");
                    form.Add(new StringContent("HTML"), "parse_mode");
                }

                if (!string.IsNullOrWhiteSpace(replyMarkupJson))
                    form.Add(new StringContent(replyMarkupJson), "reply_markup");

                using var response = await client.PostAsync(url, form, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "Telegram sendPhoto failed (chat {ChatId}): {StatusCode} {Body}",
                        chatId, (int)response.StatusCode, body);
                    return false;
                }

                var result = System.Text.Json.JsonSerializer.Deserialize<TelegramApiResponse>(body);
                if (result is not { Ok: true })
                {
                    _logger.LogError("Telegram sendPhoto returned ok=false (chat {ChatId}): {Body}", chatId, body);
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Telegram sendPhoto threw (chat {ChatId})", chatId);
                return false;
            }
        }

        /// <summary>Discounts strictly above this % use the premium Mega Deal image card.</summary>
        private const decimal MegaDealDropPercent = 50m;

        /// <summary>Telegram <c>copy_text</c> button payload max length.</summary>
        private const int TelegramCopyTextMaxLength = 256;

        private static string? BuildProductUrl(string? asin) =>
            string.IsNullOrWhiteSpace(asin)
                ? null
                : $"https://www.amazon.eg/dp/{asin.Trim()}?language=ar_AE";

        /// <summary>
        /// Inline keyboard: copy product name + open Amazon (separate buttons).
        /// Uses Telegram <c>copy_text</c> (clipboard) and <c>url</c>.
        /// </summary>
        private static string? BuildAlertReplyMarkupJson(string? productName, string? productUrl)
        {
            var rows = new List<object[]>();

            var name = productName?.Trim() ?? "";
            if (name.Length > 0)
            {
                if (name.Length > TelegramCopyTextMaxLength)
                    name = name[..TelegramCopyTextMaxLength];

                rows.Add(
                [
                    new Dictionary<string, object>
                    {
                        ["text"] = "📋 نسخ اسم المنتج",
                        ["copy_text"] = new Dictionary<string, string> { ["text"] = name }
                    }
                ]);
            }

            if (!string.IsNullOrWhiteSpace(productUrl))
            {
                rows.Add(
                [
                    new Dictionary<string, object>
                    {
                        ["text"] = "🛒 فتح أمازون",
                        ["url"] = productUrl
                    }
                ]);
            }

            if (rows.Count == 0)
                return null;

            return System.Text.Json.JsonSerializer.Serialize(new { inline_keyboard = rows });
        }

        private static System.Text.Json.JsonElement? ParseReplyMarkup(string? replyMarkupJson)
        {
            if (string.IsNullOrWhiteSpace(replyMarkupJson))
                return null;

            return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(replyMarkupJson);
        }

        /// <summary>Caption under the Mega Deal image: price history only (name is on the card; CTAs are buttons).</summary>
        private static string BuildMegaDealCaption(ProductDropAlert alert)
        {
            var currency = Html(alert.Product.Currency ?? "");
            var sb = new StringBuilder();
            AppendPriceHistory(sb, alert, currency, megaDeal: true);
            var caption = sb.ToString().Trim();

            // Telegram caption limit is 1024 characters.
            if (caption.Length > 1024)
                caption = caption[..1021] + "…";

            return caption;
        }

        private async Task<byte[]?> TryDownloadProductImageAsync(string? imageUrl, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(imageUrl) ||
                !Uri.TryCreate(imageUrl.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (compatible; AffiliateBot/1.0)");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return null;

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                // Keep Mega Deal sends snappy — skip huge assets.
                return bytes.Length is > 0 and <= 2_500_000 ? bytes : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Mega Deal product image download failed: {Url}", imageUrl);
                return null;
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
            AppendProductHeader(sb, product, name, asin);
            sb.AppendLine($"السعر الحالي: <b>{alert.CurrentPrice} {currency}</b>");

            if (alert.BaselinePrice.HasValue)
                sb.AppendLine($"السعر السابق: {alert.BaselinePrice} {currency}");

            if (alert.AveragePrice.HasValue)
                sb.AppendLine($"متوسط السعر: <b>{alert.AveragePrice:0.##} {currency}</b>");

            AppendPriceHistory(sb, alert, currency, megaDeal: false);
            return sb.ToString().TrimEnd();
        }

        private static void AppendProductHeader(StringBuilder sb, Product product, string name, string asin)
        {
            sb.AppendLine(string.IsNullOrWhiteSpace(product.Asin)
                ? $"<b>{name}</b>"
                : $"<a href=\"{Html($"https://www.amazon.eg/dp/{product.Asin.Trim()}?language=ar_AE")}\">{name}</a>");
            sb.AppendLine($"ASIN: <code>{asin}</code>");
        }

        private static void AppendPriceHistory(
            StringBuilder sb,
            ProductDropAlert alert,
            string currency,
            bool megaDeal)
        {
            if (alert.History.Count == 0)
                return;

            sb.AppendLine();
            sb.AppendLine(megaDeal
                ? "📉 <b>سجل الأسعار · UTC</b>"
                : "📉 <b>سجل الأسعار (بتوقيت UTC):</b>");

            var history = alert.History;
            var showEllipsis = alert.HistoryTruncated && history.Count >= 10;
            var firstCount = showEllipsis ? 5 : history.Count;
            var lastStart = showEllipsis ? history.Count - 5 : history.Count;
            var bullet = megaDeal ? "◆" : "•";

            for (var i = 0; i < firstCount; i++)
                AppendHistoryLine(sb, history[i], currency, bullet);

            if (showEllipsis)
            {
                sb.AppendLine($"{bullet} …");
                for (var i = lastStart; i < history.Count; i++)
                    AppendHistoryLine(sb, history[i], currency, bullet);
            }
        }

        private static void AppendHistoryLine(
            StringBuilder sb,
            PriceHistoryPoint point,
            string currency,
            string bullet = "•")
        {
            var price = point.Price.HasValue ? $"{point.Price} {currency}" : "—";
            var date = point.CheckedAt.HasValue ? $"{point.CheckedAt:yyyy-MM-dd HH:mm}" : null;
            if (!string.IsNullOrWhiteSpace(point.Label))
                sb.AppendLine($"{bullet} {point.Label}: {price}" + (date is null ? "" : $" — {date}"));
            else
                sb.AppendLine(date is null ? $"{bullet} {price}" : $"{bullet} {price} — {date}");
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

            [JsonPropertyName("reply_markup")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public System.Text.Json.JsonElement? ReplyMarkup { get; set; }
        }

        private sealed class TelegramApiResponse
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; set; }
        }
    }
}
