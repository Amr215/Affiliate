using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Web;
using Affiliate.Models;
using Affiliate.Noon;
using Affiliate.Noon.Models;
using Affiliate.Options;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    public interface ITelegramNotifier
    {
        /// <summary>Returns true when the price-drop message was delivered to the main chat.</summary>
        Task<bool> NotifyDropAsync(ProductDropAlert alert, CancellationToken cancellationToken = default);

        /// <summary>Sends a Noon price drop to the Noon chat.</summary>
        Task<bool> NotifyNoonDropAsync(NoonDropAlert alert, CancellationToken cancellationToken = default);

        /// <summary>Sends a plain test message to verify BotToken/ChatId.</summary>
        Task<(bool Success, string Detail)> SendTestMessageAsync(CancellationToken cancellationToken = default);

        /// <summary>Plain text reply (e.g. prepare-for-publish failure).</summary>
        Task<bool> SendPlainTextAsync(
            string chatId,
            string text,
            int? replyToMessageId = null,
            CancellationToken cancellationToken = default);

        /// <summary>Publish-ready photo: product name + URL caption and copy-text button.</summary>
        Task<bool> SendPreparePublishAsync(
            string chatId,
            string productName,
            string productUrl,
            byte[] screenshotPng,
            string? captionNote = null,
            int? replyToMessageId = null,
            bool cartScreenshot = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Replies to a posted Amazon link with the publish template buttons.
        /// <paramref name="target"/> is an ASIN or <c>u:CODE</c> for a generic short link.
        /// </summary>
        Task<bool> SendPublishOptionsAsync(
            string chatId,
            string target,
            int replyToMessageId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Publish captioned with the template of <paramref name="kind"/> (offer / greatoffer / snap / free).
        /// Sends a photo when <paramref name="screenshotPng"/> is set; otherwise a text message.
        /// </summary>
        Task<bool> SendPublishPostAsync(
            string chatId,
            string kind,
            string productName,
            string productUrl,
            decimal? productPrice,
            byte[]? screenshotPng,
            string? captionNote = null,
            int? replyToMessageId = null,
            bool cartScreenshot = false,
            CancellationToken cancellationToken = default);

        /// <summary>HTML caption for a publish template, or null when <paramref name="kind"/> is unknown.</summary>
        string? BuildPublishCaptionHtml(
            string kind,
            string productName,
            string productUrl,
            decimal? productPrice,
            string? captionNote = null);

        /// <summary>Preview photo with confirm/cancel buttons; returns Telegram message_id when sent.</summary>
        Task<int?> SendPublishPreviewAsync(
            string chatId,
            string captionHtml,
            byte[] screenshotPng,
            string token,
            bool cartScreenshot = false,
            int? replyToMessageId = null,
            CancellationToken cancellationToken = default);

        /// <summary>Final publish using a ready caption; photo when <paramref name="screenshotPng"/> is set.</summary>
        Task<bool> SendPublishCaptionAsync(
            string chatId,
            string captionHtml,
            byte[]? screenshotPng = null,
            bool cartScreenshot = false,
            int? replyToMessageId = null,
            CancellationToken cancellationToken = default);

        Task<bool> EditMessageCaptionAsync(
            string chatId,
            int messageId,
            string captionHtml,
            string? replyMarkupJson = null,
            CancellationToken cancellationToken = default);

        Task<bool> ClearMessageReplyMarkupAsync(
            string chatId,
            int messageId,
            CancellationToken cancellationToken = default);

        string BuildPublishConfirmKeyboardJson(string token);
    }

    public sealed class ProductDropAlert : PriceDropAlert
    {
        public required Product Product { get; init; }

        /// <summary>Category of the URL scrape that owns the product; null alerts are not published.</summary>
        public ScraperCategory? Category { get; set; }
    }

    public sealed class NoonDropAlert : PriceDropAlert
    {
        public required NoonProduct Product { get; init; }
    }

    /// <summary>The same product in the other store, shown under the alert.</summary>
    public sealed record LinkedOffer(string Store, decimal? Price, string Url, bool IsAvailable);

    public abstract class PriceDropAlert
    {
        /// <summary>Percent drop of the current price versus the previous recorded price.</summary>
        public required decimal DropPercent { get; init; }

        /// <summary>
        /// Min drop % from the owning scrape URL / brand; also used as the below-average gate
        /// for drops under 10%.
        /// </summary>
        public decimal AlertThresholdPercent { get; init; } = PriceDropRules.PriceDropAlertThresholdPercent;

        /// <summary>Linked product in the other store, when one is linked.</summary>
        public LinkedOffer? Linked { get; set; }

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

        /// <summary>Lowest of all recorded prices (not only the truncated History window).</summary>
        public decimal? LowestPrice { get; set; }

        /// <summary>True when middle history points were omitted from <see cref="History"/>.</summary>
        public bool HistoryTruncated { get; set; }

        /// <summary>Whole days the current price has been the lowest recorded price.</summary>
        public int LowestPriceDays { get; set; }

        /// <summary>True when no lower price was ever recorded (ties with a prior low still count).</summary>
        public bool IsLowestEver { get; set; }

        /// <summary>True when the current price is strictly below every earlier recorded price.</summary>
        public bool IsUniqueLowestEver { get; set; }
    }

    /// <summary>A price-history data point rendered inside a drop alert (label optional).</summary>
    public sealed record PriceHistoryPoint(string Label, decimal? Price, DateTime? CheckedAt);

    public sealed class TelegramNotifier : ITelegramNotifier
    {
        public const string HttpClientName = "TelegramBot";

        private readonly TelegramOptions _options;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AsinLinkCodec _asinLinkCodec;
        private readonly NoonSkuLinkCodec _noonSkuLinkCodec;
        private readonly ILogger<TelegramNotifier> _logger;

        public TelegramNotifier(
            IOptions<TelegramOptions> options,
            IHttpClientFactory httpClientFactory,
            AsinLinkCodec asinLinkCodec,
            NoonSkuLinkCodec noonSkuLinkCodec,
            ILogger<TelegramNotifier> logger)
        {
            _options = options.Value;
            _httpClientFactory = httpClientFactory;
            _asinLinkCodec = asinLinkCodec;
            _noonSkuLinkCodec = noonSkuLinkCodec;
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

            // Publish only to the group of the product's category; uncategorised URLs stay silent.
            var categoryChatId = ResolveCategoryChatId(alert.Category);
            if (categoryChatId is null)
            {
                _logger.LogInformation(
                    "No Telegram chat for category {Category} — alert not sent: {Asin} {Percent}%",
                    alert.Category, alert.Product.Asin, alert.DropPercent);
                return false;
            }

            var productUrl = BuildProductUrl(alert.Product.Asin);
            var replyMarkup = BuildAlertReplyMarkupJson(
                alert.Product.Name,
                productUrl,
                alert.Product.Asin,
                alert.CurrentPrice);
            var text = BuildDropHtml(alert, AmazonProductHeader(alert.Product), alert.Product.Currency);

            // Mega deals used a colored card image; kept commented out until the card design is finalised.
            // if (alert.DropPercent > MegaDealDropPercent)
            // {
            //     var productImageBytes = await TryDownloadProductImageAsync(alert.Product.ImageUrl, cancellationToken);
            //     var png = MegaDealCardImage.Render(alert, productImageBytes);
            //     var caption = BuildMegaDealCaption(alert);
            //     sent = await SendPhotoAsync(
            //         chatId,
            //         png,
            //         caption,
            //         replyMarkup,
            //         replyToMessageId: null,
            //         photoFileName: "mega-deal.png",
            //         cancellationToken: cancellationToken);
            // }

            var sent = await SendMessageAsync(categoryChatId, text, replyMarkup, replyToMessageId: null, cancellationToken);

            // Drops above 50% are mirrored to the cross-category mega deals group.
            if (alert.DropPercent > MegaDealDropPercent && NullIfBlank(_options.MegaDealsChatId) is { } megaChatId)
                sent |= await SendMessageAsync(megaChatId.Trim(), text, replyMarkup, replyToMessageId: null, cancellationToken);

            // Unique lowest-ever goes only to the unique chat (not also the lowest-price chat).
            if (alert.IsUniqueLowestEver && NullIfBlank(_options.UniqueLowestPriceChatId) is { } uniqueLowestChatId)
                sent |= await SendMessageAsync(uniqueLowestChatId.Trim(), text, replyMarkup, replyToMessageId: null, cancellationToken);
            else if (QualifiesForLowestPriceGroup(alert) && NullIfBlank(_options.LowestPriceChatId) is { } lowestChatId)
                sent |= await SendMessageAsync(lowestChatId.Trim(), text, replyMarkup, replyToMessageId: null, cancellationToken);

            if (sent)
                _logger.LogInformation(
                    "Telegram drop alert sent for {Asin} ({Percent}% → {Category} chat {ChatId})",
                    alert.Product.Asin, alert.DropPercent, alert.Category, categoryChatId);
            else
                _logger.LogWarning(
                    "Telegram drop alert failed for {Asin} ({Percent}% → {Category} chat {ChatId})",
                    alert.Product.Asin, alert.DropPercent, alert.Category, categoryChatId);

            return sent;
        }

        public async Task<bool> NotifyNoonDropAsync(NoonDropAlert alert, CancellationToken cancellationToken = default)
        {
            var product = alert.Product;
            if (!_options.Enabled || NullIfBlank(_options.BotToken) is null || NullIfBlank(_options.NoonChatId) is not { } chatId)
            {
                _logger.LogInformation(
                    "Telegram disabled or not configured for Noon — Noon drop alert not sent: {Sku} {Percent}%",
                    product.Sku, alert.DropPercent);
                return false;
            }

            // Alerts open the Arabic storefront (egypt-ar), even when the scraped URL is egypt-en.
            var productUrl = NoonPrepareForPublishService.ToArabicLocaleUrl(product.Url);
            var header = $"<a href=\"{Html(productUrl)}\">{Html(product.Name)}</a>\nSKU: <code>{Html(product.Sku)}</code>\n";
            var text = BuildDropHtml(alert, header, product.Currency);
            var replyMarkup = BuildAlertReplyMarkupJson(product.Name, productUrl, null, alert.CurrentPrice, "🛒 فتح نون");

            var sent = await SendMessageAsync(chatId.Trim(), text, replyMarkup, replyToMessageId: null, cancellationToken);

            // Same criteria as Amazon: >50% → mega deals; lowest-ever or lowest for ≥N days → lowest-price group.
            if (alert.DropPercent > MegaDealDropPercent && NullIfBlank(_options.NoonMegaDealsChatId) is { } megaChatId)
                sent |= await SendMessageAsync(megaChatId.Trim(), text, replyMarkup, replyToMessageId: null, cancellationToken);

            // Unique lowest-ever goes only to the unique chat (not also the lowest-price chat).
            if (alert.IsUniqueLowestEver && NullIfBlank(_options.NoonUniqueLowestPriceChatId) is { } uniqueLowestChatId)
                sent |= await SendMessageAsync(uniqueLowestChatId.Trim(), text, replyMarkup, replyToMessageId: null, cancellationToken);
            else if (QualifiesForLowestPriceGroup(alert) && NullIfBlank(_options.NoonLowestPriceChatId) is { } lowestChatId)
                sent |= await SendMessageAsync(lowestChatId.Trim(), text, replyMarkup, replyToMessageId: null, cancellationToken);

            _logger.Log(
                sent ? LogLevel.Information : LogLevel.Warning,
                "Telegram Noon drop alert {Result} for {Sku} ({Percent}%)",
                sent ? "sent" : "failed", product.Sku, alert.DropPercent);
            return sent;
        }

        public async Task<(bool Success, string Detail)> SendTestMessageAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConfigured(out var detail))
                return (false, detail);

            var text =
                $"✅ <b>Affiliate</b> Telegram is connected.\n" +
                $"Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";

            var sent = await SendMessageAsync(
                _options.PrimaryChatId!.Trim(),
                text,
                replyMarkupJson: null,
                replyToMessageId: null,
                cancellationToken);
            return sent
                ? (true, "Test message sent successfully.")
                : (false, "Telegram API rejected the request. Check logs and BotToken/ChatId.");
        }

        public Task<bool> SendPlainTextAsync(
            string chatId,
            string text,
            int? replyToMessageId = null,
            CancellationToken cancellationToken = default) =>
            SendMessageAsync(chatId, Html(text), replyMarkupJson: null, replyToMessageId, cancellationToken);

        public async Task<bool> SendPreparePublishAsync(
            string chatId,
            string productName,
            string productUrl,
            byte[] screenshotPng,
            string? captionNote = null,
            int? replyToMessageId = null,
            bool cartScreenshot = false,
            CancellationToken cancellationToken = default)
        {
            var name = productName?.Trim() ?? "";
            var url = ShortenProductUrl(productUrl?.Trim() ?? "");
            var note = captionNote?.Trim() ?? "";
            var caption = string.IsNullOrEmpty(note)
                ? $"{Html(name)}\n{Html(url)}"
                : $"{Html(name)}\n{Html(note)}\n{Html(url)}";
            if (caption.Length > 1024)
                caption = caption[..1021] + "…";

            var copyPayload = string.IsNullOrEmpty(note)
                ? $"{name}\n{url}".Trim()
                : $"{name}\n{note}\n{url}".Trim();
            if (copyPayload.Length > TelegramCopyTextMaxLength)
                copyPayload = copyPayload[..TelegramCopyTextMaxLength];

            string? replyMarkup = null;
            if (copyPayload.Length > 0)
            {
                replyMarkup = System.Text.Json.JsonSerializer.Serialize(new
                {
                    inline_keyboard = new[]
                    {
                        new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["text"] = "📋 نسخ الاسم والرابط",
                                ["copy_text"] = new Dictionary<string, string> { ["text"] = copyPayload }
                            }
                        }
                    }
                });
            }

            var (ok, _) = await SendPhotoAsync(
                chatId,
                screenshotPng,
                caption,
                replyMarkup,
                replyToMessageId: replyToMessageId,
                photoFileName: "prepare-publish.png",
                cartScreenshot: cartScreenshot,
                cancellationToken: cancellationToken);
            return ok;
        }

        public Task<bool> SendPublishOptionsAsync(
            string chatId,
            string target,
            int replyToMessageId,
            CancellationToken cancellationToken = default)
        {
            object Button(string text, string kind) => new[]
            {
                new Dictionary<string, object>
                {
                    ["text"] = text,
                    ["callback_data"] = $"{TelegramCallbackBackgroundService.PublishCallbackPrefix}{kind}:{target}"
                }
            };

            var replyMarkup = System.Text.Json.JsonSerializer.Serialize(new
            {
                inline_keyboard = new[] { Button("نشر عرض", "offer"), Button("نشر عرض ممتاز", "greatoffer"), Button("نشر لقطة", "snap"), Button("نشر ببلاش", "free") }
            });

            return SendMessageAsync(chatId, Html($"اختر قالب النشر ({target}):"), replyMarkup, replyToMessageId, cancellationToken);
        }

        public const string PublishEditHint = "للتعديل: رد على هذه الرسالة";

        public string? BuildPublishCaptionHtml(
            string kind,
            string productName,
            string productUrl,
            decimal? productPrice,
            string? captionNote = null)
        {
            var name = productName.Trim();
            if (productPrice.HasValue)
                name += $" بسعر {Math.Truncate(productPrice.Value):0} جنية";
            var url = BuildAffiliateProductUrl(productUrl.Trim());
            var note = captionNote?.Trim() ?? "";
            var beforeUrl = string.IsNullOrEmpty(note) ? "" : $"\n\n{note}";
            var caption = kind switch
            {
                "offer" => $"عرض على {name}{beforeUrl}\n\n{url}",
                "greatoffer" => $"عرض ممتاز على {name}{beforeUrl}\n\n{url}",
                "snap" => $"لقطة\n\n{name}{beforeUrl}\n\n{url}",
                "free" => $"ببلاش\n\n{name}{beforeUrl}\n\n{url}",
                _ => null
            };
            if (caption is null)
                return null;

            caption = Html(caption);
            return caption.Length > 1024 ? caption[..1021] + "…" : caption;
        }

        public string BuildPublishConfirmKeyboardJson(string token)
        {
            object Row(string text, string prefix) => new[]
            {
                new Dictionary<string, object>
                {
                    ["text"] = text,
                    ["callback_data"] = prefix + token
                }
            };

            return System.Text.Json.JsonSerializer.Serialize(new
            {
                inline_keyboard = new[]
                {
                    Row("تأكيد مع صورة", TelegramCallbackBackgroundService.PublishConfirmPrefix),
                    Row("تأكيد بدون صورة", TelegramCallbackBackgroundService.PublishConfirmTextPrefix),
                    Row("إلغاء", TelegramCallbackBackgroundService.PublishCancelPrefix)
                }
            });
        }

        public async Task<int?> SendPublishPreviewAsync(
            string chatId,
            string captionHtml,
            byte[] screenshotPng,
            string token,
            bool cartScreenshot = false,
            int? replyToMessageId = null,
            CancellationToken cancellationToken = default)
        {
            var previewCaption = AppendPublishEditHint(captionHtml);
            var (_, messageId) = await SendPhotoAsync(
                chatId,
                screenshotPng,
                previewCaption,
                BuildPublishConfirmKeyboardJson(token),
                replyToMessageId,
                photoFileName: "publish-preview.png",
                cartScreenshot: cartScreenshot,
                cancellationToken: cancellationToken);
            return messageId;
        }

        public async Task<bool> SendPublishCaptionAsync(
            string chatId,
            string captionHtml,
            byte[]? screenshotPng = null,
            bool cartScreenshot = false,
            int? replyToMessageId = null,
            CancellationToken cancellationToken = default)
        {
            if (screenshotPng is not { Length: > 0 })
                return await SendMessageAsync(chatId, captionHtml, null, replyToMessageId, cancellationToken);

            var (ok, _) = await SendPhotoAsync(
                chatId,
                screenshotPng,
                captionHtml,
                replyMarkupJson: null,
                replyToMessageId,
                photoFileName: "publish.png",
                cartScreenshot,
                cancellationToken);
            return ok;
        }

        public async Task<bool> SendPublishPostAsync(
            string chatId,
            string kind,
            string productName,
            string productUrl,
            decimal? productPrice,
            byte[]? screenshotPng,
            string? captionNote = null,
            int? replyToMessageId = null,
            bool cartScreenshot = false,
            CancellationToken cancellationToken = default)
        {
            var caption = BuildPublishCaptionHtml(kind, productName, productUrl, productPrice, captionNote);
            if (caption is null)
                return false;

            return await SendPublishCaptionAsync(
                chatId, caption, screenshotPng, cartScreenshot, replyToMessageId, cancellationToken);
        }

        public async Task<bool> EditMessageCaptionAsync(
            string chatId,
            int messageId,
            string captionHtml,
            string? replyMarkupJson = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                var url = $"https://api.telegram.org/bot{_options.BotToken.Trim()}/editMessageCaption";
                var payload = new Dictionary<string, object?>
                {
                    ["chat_id"] = chatId,
                    ["message_id"] = messageId,
                    ["caption"] = AppendPublishEditHint(captionHtml),
                    ["parse_mode"] = "HTML"
                };
                if (!string.IsNullOrWhiteSpace(replyMarkupJson))
                    payload["reply_markup"] = ParseReplyMarkup(replyMarkupJson);

                using var response = await client.PostAsJsonAsync(url, payload, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Telegram editMessageCaption threw (chat {ChatId})", chatId);
                return false;
            }
        }

        public async Task<bool> ClearMessageReplyMarkupAsync(
            string chatId,
            int messageId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                var url = $"https://api.telegram.org/bot{_options.BotToken.Trim()}/editMessageReplyMarkup";
                var payload = new Dictionary<string, object?>
                {
                    ["chat_id"] = chatId,
                    ["message_id"] = messageId,
                    ["reply_markup"] = new { inline_keyboard = Array.Empty<object[]>() }
                };
                using var response = await client.PostAsJsonAsync(url, payload, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Telegram editMessageReplyMarkup threw (chat {ChatId})", chatId);
                return false;
            }
        }

        public static string AppendPublishEditHint(string captionHtml)
        {
            var withHint = string.IsNullOrEmpty(captionHtml)
                ? PublishEditHint
                : $"{captionHtml}\n\n{PublishEditHint}";
            return withHint.Length > 1024 ? withHint[..1021] + "…" : withHint;
        }

        public static string ClampCaptionHtml(string captionHtml) =>
            captionHtml.Length > 1024 ? captionHtml[..1021] + "…" : captionHtml;

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

        /// <summary>Maps a product category to its group chat; null when unset or unconfigured.</summary>
        private string? ResolveCategoryChatId(ScraperCategory? category) => category switch
        {
            ScraperCategory.FoodDrinksAndCleaning => NullIfBlank(_options.FoodDrinksAndCleaningChatId)?.Trim(),
            ScraperCategory.FashionAndWatches => NullIfBlank(_options.FashionAndWatchesChatId)?.Trim(),
            ScraperCategory.Devices => NullIfBlank(_options.DevicesChatId)?.Trim(),
            _ => null
        };

        private static string? NullIfBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;

        private async Task<bool> SendMessageAsync(
            string chatId,
            string text,
            string? replyMarkupJson,
            int? replyToMessageId,
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
                    ReplyMarkup = ParseReplyMarkup(replyMarkupJson),
                    ReplyToMessageId = replyToMessageId
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

        private async Task<(bool Ok, int? MessageId)> SendPhotoAsync(
            string chatId,
            byte[] pngBytes,
            string? captionHtml,
            string? replyMarkupJson,
            int? replyToMessageId = null,
            string photoFileName = "mega-deal.png",
            bool cartScreenshot = false,
            CancellationToken cancellationToken = default)
        {
            try
            {
                pngBytes = ImageLogoOverlay.Apply(pngBytes, cartScreenshot);

                var client = _httpClientFactory.CreateClient(HttpClientName);
                var url = $"https://api.telegram.org/bot{_options.BotToken.Trim()}/sendPhoto";

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(chatId), "chat_id");

                var photoContent = new ByteArrayContent(pngBytes);
                photoContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                form.Add(photoContent, "photo", photoFileName);

                if (!string.IsNullOrWhiteSpace(captionHtml))
                {
                    form.Add(new StringContent(captionHtml), "caption");
                    form.Add(new StringContent("HTML"), "parse_mode");
                }

                if (!string.IsNullOrWhiteSpace(replyMarkupJson))
                    form.Add(new StringContent(replyMarkupJson), "reply_markup");

                if (replyToMessageId is > 0)
                    form.Add(new StringContent(replyToMessageId.Value.ToString()), "reply_to_message_id");

                using var response = await client.PostAsync(url, form, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "Telegram sendPhoto failed (chat {ChatId}): {StatusCode} {Body}",
                        chatId, (int)response.StatusCode, body);
                    return (false, null);
                }

                var result = System.Text.Json.JsonSerializer.Deserialize<TelegramApiResponse>(body);
                if (result is not { Ok: true })
                {
                    _logger.LogError("Telegram sendPhoto returned ok=false (chat {ChatId}): {Body}", chatId, body);
                    return (false, null);
                }

                return (true, result.Result?.MessageId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Telegram sendPhoto threw (chat {ChatId})", chatId);
                return (false, null);
            }
        }

        /// <summary>Discounts strictly above this % use the premium Mega Deal image card.</summary>
        private const decimal MegaDealDropPercent = 50m;

        /// <summary>
        /// Drops that are the lowest price for at least this many days also go to the lowest-price group.
        /// Newer products (tracked fewer days) still qualify when the price is the lowest ever.
        /// </summary>
        private const int LowestPriceGroupMinDays = 30;

        /// <summary>
        /// Lowest for ≥ <see cref="LowestPriceGroupMinDays"/> days, or lowest for the product's
        /// entire history when it is younger than that window.
        /// </summary>
        private static bool QualifiesForLowestPriceGroup(PriceDropAlert alert) =>
            alert.IsLowestEver || alert.LowestPriceDays >= LowestPriceGroupMinDays;

        /// <summary>Telegram <c>copy_text</c> button payload max length.</summary>
        private const int TelegramCopyTextMaxLength = 256;

        private static string? BuildProductUrl(string? asin) =>
            string.IsNullOrWhiteSpace(asin)
                ? null
                : $"https://{BuildShortProductUrl(asin)}";

        private static string BuildShortProductUrl(string asin) => $"amazon.eg/dp/{asin.Trim()}";

        private static readonly System.Text.RegularExpressions.Regex DpAsinRegex =
            new(@"/dp/([A-Za-z0-9]{10})", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Rewrites any Amazon product URL to <c>amazon.eg/dp/ASIN</c>; other URLs are returned unchanged.</summary>
        private static string ShortenProductUrl(string url)
        {
            var match = DpAsinRegex.Match(url);
            return match.Success ? BuildShortProductUrl(match.Groups[1].Value) : url;
        }

        /// <summary>
        /// Site redirect link for publish posts, e.g. <c>https://el-sindbad.com/xK9mPq2Ab</c>
        /// (opaque code; Amazon tag is applied by the short-link endpoint, not in the caption).
        /// Already-short <c>/u/…</c> (Amazon) and <c>/n/…</c> (Noon) links are returned unchanged.
        /// </summary>
        private string BuildAffiliateProductUrl(string url)
        {
            if (url.Contains("/u/", StringComparison.OrdinalIgnoreCase)
                || url.Contains("/n/", StringComparison.OrdinalIgnoreCase))
                return url;

            var match = DpAsinRegex.Match(url);
            if (match.Success)
            {
                var baseUrl = (_options.PublishLinkBaseUrl ?? "https://el-sindbad.com").Trim().TrimEnd('/');
                if (baseUrl.Length == 0)
                    baseUrl = "https://el-sindbad.com";

                var code = _asinLinkCodec.Encode(match.Groups[1].Value);
                return $"{baseUrl}/{code}";
            }

            // Noon product → opaque SKU code (no DB), same idea as Amazon ASIN codes.
            if (NoonPrepareForPublishService.TryResolveSkuAndUrl(url, out var sku, out _))
                return _noonSkuLinkCodec.BuildPublicUrl(sku);

            return ShortenProductUrl(url);
        }

        /// <summary>
        /// Inline keyboard: copy product name + open Amazon.
        /// («تجهيز للنشر» is temporarily commented out.)
        /// </summary>
        private static string? BuildAlertReplyMarkupJson(
            string? productName,
            string? productUrl,
            string? asin,
            decimal? currentPrice,
            string openLabel = "🛒 فتح أمازون")
        {
            var rows = new List<object[]>();

            // Temporarily disabled — re-enable when prepare-for-publish is ready to ship.
            // if (!string.IsNullOrWhiteSpace(asin))
            // {
            //     var callback = BuildPrepCallbackData(asin, currentPrice);
            //     if (callback is not null)
            //     {
            //         rows.Add(
            //         [
            //             new Dictionary<string, object>
            //             {
            //                 ["text"] = "تجهيز للنشر",
            //                 ["callback_data"] = callback
            //             }
            //         ]);
            //     }
            // }

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

                rows.Add(
                [
                    new Dictionary<string, object>
                    {
                        ["text"] = "🔍 بحث جوجل",
                        ["url"] = $"https://www.google.com/search?q={Uri.EscapeDataString(name)}"
                    }
                ]);
            }

            if (!string.IsNullOrWhiteSpace(productUrl))
            {
                rows.Add(
                [
                    new Dictionary<string, object>
                    {
                        ["text"] = "🔗 نسخ الرابط",
                        ["copy_text"] = new Dictionary<string, string> { ["text"] = ShortenProductUrl(productUrl) }
                    }
                ]);

                rows.Add(
                [
                    new Dictionary<string, object>
                    {
                        ["text"] = openLabel,
                        ["url"] = productUrl
                    }
                ]);
            }

            if (rows.Count == 0)
                return null;

            return System.Text.Json.JsonSerializer.Serialize(new { inline_keyboard = rows });
        }

        /// <summary>Telegram callback_data max 64 bytes: prep:ASIN:price</summary>
        private static string? BuildPrepCallbackData(string asin, decimal? currentPrice)
        {
            var cleanedAsin = asin.Trim().ToUpperInvariant();
            if (cleanedAsin.Length is < 8 or > 16)
                return null;

            var data = currentPrice.HasValue
                ? $"prep:{cleanedAsin}:{currentPrice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : $"prep:{cleanedAsin}";

            return data.Length <= 64 ? data : $"prep:{cleanedAsin}";
        }

        private static System.Text.Json.JsonElement? ParseReplyMarkup(string? replyMarkupJson)
        {
            if (string.IsNullOrWhiteSpace(replyMarkupJson))
                return null;

            return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(replyMarkupJson);
        }

        /// <summary>
        /// Caption under the Mega Deal image: price history only (name is on the card; CTAs are buttons).
        /// Unused while the Mega Deal card path in <see cref="NotifyDropAsync"/> is commented out.
        /// </summary>
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

        /// <summary>Unused while the Mega Deal card path in <see cref="NotifyDropAsync"/> is commented out.</summary>
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

        /// <param name="productHeaderHtml">Product link and id lines, each ending with a newline.</param>
        private static string BuildDropHtml(PriceDropAlert alert, string productHeaderHtml, string? productCurrency)
        {
            var currency = Html(productCurrency ?? "");
            var sb = new StringBuilder();

            sb.AppendLine($"🔻 <b>انخفاض السعر {alert.DropPercent:0.#}%</b>");
            if (alert.IsLowestEver)
                sb.AppendLine("🏆 <b><u>أقل سعر على الإطلاق</u></b>");
            else if (alert.LowestPriceDays >= LowestPriceGroupMinDays)
                sb.AppendLine($"🔥 <b><u>أقل سعر منذ {alert.LowestPriceDays} يوم</u></b>");
            sb.AppendLine();
            sb.Append(productHeaderHtml);
            sb.AppendLine($"السعر الحالي: <b>{alert.CurrentPrice} {currency}</b>");

            if (alert.BaselinePrice.HasValue)
                sb.AppendLine($"السعر السابق: {alert.BaselinePrice} {currency}");

            if (alert.LowestPrice.HasValue)
                sb.AppendLine($"أقل سعر: <b>{alert.LowestPrice:0.##} {currency}</b>");

            if (alert.AveragePrice.HasValue)
                sb.AppendLine($"متوسط السعر: <b>{alert.AveragePrice:0.##} {currency}</b>");

            if (alert.Linked is { } linked)
            {
                var price = linked.Price.HasValue ? $"{linked.Price:0.##} {currency}" : "—";
                var availability = linked.IsAvailable ? "" : " (غير متاح)";
                sb.AppendLine($"السعر على {Html(linked.Store)}: <a href=\"{Html(linked.Url)}\">{price}</a>{availability}");
            }

            AppendPriceHistory(sb, alert, currency, megaDeal: false);
            return sb.ToString().TrimEnd();
        }

        private static string AmazonProductHeader(Product product)
        {
            var name = Html(product.Name);
            var link = string.IsNullOrWhiteSpace(product.Asin)
                ? $"<b>{name}</b>"
                : $"<a href=\"{Html(BuildProductUrl(product.Asin))}\">{name}</a>";
            return $"{link}\nASIN: <code>{Html(product.Asin ?? "")}</code>\n";
        }

        private static void AppendPriceHistory(
            StringBuilder sb,
            PriceDropAlert alert,
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

            [JsonPropertyName("reply_to_message_id")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? ReplyToMessageId { get; set; }
        }

        private sealed class TelegramApiResponse
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; set; }

            [JsonPropertyName("result")]
            public TelegramMessageResult? Result { get; set; }
        }

        private sealed class TelegramMessageResult
        {
            [JsonPropertyName("message_id")]
            public int MessageId { get; set; }
        }
    }
}
