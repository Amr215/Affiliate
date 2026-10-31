using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Affiliate.Data;
using Affiliate.Noon.Models;
using Affiliate.Options;
using Affiliate.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Affiliate.Noon
{
    /// <summary>
    /// Handles Noon product links posted in <see cref="TelegramOptions.NoonPublishChatId"/>
    /// and the matching publish callback buttons (separate prefixes from Amazon).
    /// </summary>
    public sealed class NoonTelegramPublishHandler(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IOptions<TelegramOptions> options,
        ILogger<NoonTelegramPublishHandler> logger)
    {
        public const string PublishCallbackPrefix = "npub:";
        public const string PublishConfirmPrefix = "npubok:";
        public const string PublishConfirmTextPrefix = "npubtxt:";
        public const string PublishCancelPrefix = "npubno:";

        private static readonly Regex NoonUrl = new(
            @"https?://(?:www\.)?noon\.com/[^\s<>]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Bare SKU in chat text (classic <c>N…</c> or partner <c>ZFC…</c>); requires a digit.</summary>
        private static readonly Regex SkuToken = new(
            @"\b(?=[A-Za-z0-9]*\d)[A-Za-z0-9]{6,32}\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public bool IsNoonPublishChat(string? chatId)
        {
            var configured = options.Value.NoonPublishChatId?.Trim() ?? "";
            return configured.Length > 0 && chatId == configured;
        }

        public bool IsNoonCallback(string data) =>
            data.StartsWith(PublishCallbackPrefix, StringComparison.OrdinalIgnoreCase)
            || data.StartsWith(PublishConfirmPrefix, StringComparison.OrdinalIgnoreCase)
            || data.StartsWith(PublishConfirmTextPrefix, StringComparison.OrdinalIgnoreCase)
            || data.StartsWith(PublishCancelPrefix, StringComparison.OrdinalIgnoreCase);

        public async Task HandleMessageAsync(
            string chatId,
            int messageId,
            string text,
            int? replyToMessageId,
            string? replyText,
            CancellationToken ct)
        {
            if (!IsNoonPublishChat(chatId))
                return;

            // Reply to a Noon preview → update caption (no re-screenshot).
            if (replyToMessageId is > 0 && !string.IsNullOrWhiteSpace(replyText))
            {
                using var editScope = scopeFactory.CreateScope();
                var drafts = editScope.ServiceProvider.GetRequiredService<NoonPublishDraftCache>();
                var telegram = editScope.ServiceProvider.GetRequiredService<ITelegramNotifier>();
                if (drafts.TryGetByPreviewMessage(chatId, replyToMessageId.Value, out var draft) && draft is not null)
                {
                    var caption = TelegramNotifier.ClampCaptionHtml(
                        HttpUtility.HtmlEncode(replyText.Trim()));
                    drafts.UpdateCaption(draft, caption);
                    await telegram.EditMessageCaptionAsync(
                        chatId,
                        draft.PreviewMessageId,
                        caption,
                        BuildConfirmKeyboardJson(draft.Token),
                        ct);
                    return;
                }
            }

            var sku = ExtractSku(text);
            if (sku is not null)
            {
                await SendPublishOptionsAsync(chatId, sku, messageId, ct);
                return;
            }

            // Noon URL without a product SKU (brand / search / deals) → short link only,
            // same idea as Amazon.eg links without an ASIN.
            var urlMatch = NoonUrl.Match(text);
            if (!urlMatch.Success)
                return;

            using var shortScope = scopeFactory.CreateScope();
            var shortLinks = shortScope.ServiceProvider.GetRequiredService<NoonShortLinkService>();
            var telegramShort = shortScope.ServiceProvider.GetRequiredService<ITelegramNotifier>();
            var publicUrl = await shortLinks.CreatePublicUrlAsync(urlMatch.Value, ct);
            if (publicUrl is null)
                return;

            await telegramShort.SendPlainTextAsync(chatId, publicUrl, messageId, ct);
        }

        public async Task HandleCallbackAsync(
            string callbackId,
            string data,
            string? chatId,
            int? replyToMessageId,
            Func<string, string, bool, CancellationToken, Task> answerCallbackAsync,
            CancellationToken ct)
        {
            if (data.StartsWith(PublishConfirmPrefix, StringComparison.OrdinalIgnoreCase))
            {
                await HandleConfirmAsync(callbackId, data[PublishConfirmPrefix.Length..], chatId, withPhoto: true, answerCallbackAsync, ct);
                return;
            }

            if (data.StartsWith(PublishConfirmTextPrefix, StringComparison.OrdinalIgnoreCase))
            {
                await HandleConfirmAsync(callbackId, data[PublishConfirmTextPrefix.Length..], chatId, withPhoto: false, answerCallbackAsync, ct);
                return;
            }

            if (data.StartsWith(PublishCancelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                await HandleCancelAsync(callbackId, data[PublishCancelPrefix.Length..], chatId, answerCallbackAsync, ct);
                return;
            }

            if (data.StartsWith(PublishCallbackPrefix, StringComparison.OrdinalIgnoreCase))
                await HandleTemplateAsync(callbackId, data, chatId, replyToMessageId, answerCallbackAsync, ct);
        }

        private async Task HandleTemplateAsync(
            string callbackId,
            string data,
            string? chatId,
            int? replyToMessageId,
            Func<string, string, bool, CancellationToken, Task> answerCallbackAsync,
            CancellationToken ct)
        {
            // npub:kind:SKU
            var parts = data.Split(':');
            if (parts.Length != 3 || string.IsNullOrWhiteSpace(chatId))
            {
                await answerCallbackAsync(callbackId, "بيانات غير صالحة", true, ct);
                return;
            }

            var (kind, sku) = (parts[1], parts[2].ToUpperInvariant());
            await answerCallbackAsync(callbackId, "جاري التجهيز للنشر…", false, ct);

            using var scope = scopeFactory.CreateScope();
            var prepare = scope.ServiceProvider.GetRequiredService<INoonPrepareForPublishService>();
            var telegram = scope.ServiceProvider.GetRequiredService<ITelegramNotifier>();
            var drafts = scope.ServiceProvider.GetRequiredService<NoonPublishDraftCache>();
            var audit = scope.ServiceProvider.GetRequiredService<INoonPublishAuditLogger>();
            var db = scope.ServiceProvider.GetRequiredService<AffiliateDbContext>();

            var correlationId = Guid.NewGuid();
            audit.Log(correlationId, sku, NoonPublishAuditEvent.Requested,
                chatId: chatId, detail: $"template={kind}");

            var known = await db.NoonProducts
                .Where(p => p.Sku == sku)
                .Select(p => new { p.CurrentPrice, p.Url })
                .FirstOrDefaultAsync(ct);

            var input = !string.IsNullOrWhiteSpace(known?.Url) ? known!.Url : sku;
            var result = await prepare.PrepareAsync(input, known?.CurrentPrice, correlationId, ct);
            if (!result.Success || result.ScreenshotPng is null)
            {
                await NotifyFailureAsync(telegram, chatId, sku, result, replyToMessageId, ct);
                return;
            }

            var productName = result.ProductName ?? sku;
            var productUrl = result.ProductUrl ?? NoonPrepareForPublishService.BuildProductUrl(sku);
            var productPrice = result.ProductPrice ?? known?.CurrentPrice;

            // Classic N… SKUs → opaque codec (no DB). Partner / ZFC… SKUs → NoonShortLinks.
            string publicUrl;
            if (NoonSkuLinkCodec.IsValidSku(sku))
            {
                var skuCodec = scope.ServiceProvider.GetRequiredService<NoonSkuLinkCodec>();
                publicUrl = skuCodec.BuildPublicUrl(sku);
            }
            else
            {
                var shortLinks = scope.ServiceProvider.GetRequiredService<NoonShortLinkService>();
                publicUrl = await shortLinks.CreatePublicUrlAsync(productUrl, ct)
                    ?? NoonPrepareForPublishService.ToArabicLocaleUrl(productUrl);
            }

            var caption = telegram.BuildPublishCaptionHtml(
                kind, productName, publicUrl, productPrice);
            if (caption is null)
            {
                await answerCallbackAsync(callbackId, "قالب غير معروف", true, ct);
                return;
            }

            var draft = drafts.Create(chatId, sku, caption, result.ScreenshotPng, correlationId);
            var previewId = await SendPreviewAsync(
                chatId, caption, result.ScreenshotPng, draft.Token, replyToMessageId, ct);
            if (previewId is > 0)
                drafts.SetPreviewMessageId(draft, previewId.Value);

            audit.Log(correlationId, sku, NoonPublishAuditEvent.PreviewSent,
                pageKind: "Dp", success: true, chatId: chatId, draftToken: draft.Token);
        }

        private async Task HandleConfirmAsync(
            string callbackId,
            string token,
            string? chatId,
            bool withPhoto,
            Func<string, string, bool, CancellationToken, Task> answerCallbackAsync,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(chatId))
            {
                await answerCallbackAsync(callbackId, "لا يمكن تحديد المحادثة", true, ct);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var drafts = scope.ServiceProvider.GetRequiredService<NoonPublishDraftCache>();
            var telegram = scope.ServiceProvider.GetRequiredService<ITelegramNotifier>();
            var audit = scope.ServiceProvider.GetRequiredService<INoonPublishAuditLogger>();

            if (!drafts.TryGet(token, out var draft) || draft is null)
            {
                await answerCallbackAsync(
                    callbackId,
                    "انتهت صلاحية المعاينة — اختر القالب مرة أخرى.",
                    true,
                    ct);
                return;
            }

            await answerCallbackAsync(callbackId, "جاري النشر…", false, ct);

            var png = withPhoto ? draft.ScreenshotPng : null;
            await telegram.SendPublishCaptionAsync(
                chatId, draft.CaptionHtml, png, cartScreenshot: false, cancellationToken: ct);

            var channelId = options.Value.PublishChannelId?.Trim() ?? "";
            if (channelId.Length > 0)
                await telegram.SendPublishCaptionAsync(
                    channelId, draft.CaptionHtml, png, cartScreenshot: false, cancellationToken: ct);

            if (draft.PreviewMessageId > 0)
                await telegram.ClearMessageReplyMarkupAsync(chatId, draft.PreviewMessageId, ct);

            audit.Log(draft.CorrelationId, draft.Sku, NoonPublishAuditEvent.Confirmed,
                pageKind: "Dp", success: true, chatId: chatId, draftToken: token,
                detail: withPhoto ? "with photo" : "text only");

            drafts.Remove(token);
        }

        private async Task HandleCancelAsync(
            string callbackId,
            string token,
            string? chatId,
            Func<string, string, bool, CancellationToken, Task> answerCallbackAsync,
            CancellationToken ct)
        {
            using var scope = scopeFactory.CreateScope();
            var drafts = scope.ServiceProvider.GetRequiredService<NoonPublishDraftCache>();
            var telegram = scope.ServiceProvider.GetRequiredService<ITelegramNotifier>();
            var audit = scope.ServiceProvider.GetRequiredService<INoonPublishAuditLogger>();

            if (drafts.TryGet(token, out var draft) && draft is not null)
            {
                if (!string.IsNullOrWhiteSpace(chatId) && draft.PreviewMessageId > 0)
                    await telegram.ClearMessageReplyMarkupAsync(chatId, draft.PreviewMessageId, ct);
                audit.Log(draft.CorrelationId, draft.Sku, NoonPublishAuditEvent.Cancelled,
                    chatId: chatId, draftToken: token);
                drafts.Remove(token);
            }

            await answerCallbackAsync(callbackId, "تم الإلغاء", false, ct);
        }

        private async Task NotifyFailureAsync(
            ITelegramNotifier telegram,
            string publishChatId,
            string sku,
            NoonPrepareForPublishResult result,
            int? replyToMessageId,
            CancellationToken ct)
        {
            await telegram.SendPlainTextAsync(
                publishChatId,
                "انتهت مهلة تحميل صفحة نون — أعد المحاولة.",
                replyToMessageId,
                ct);

            var logChatId = options.Value.PublishLogChatId?.Trim() ?? "";
            if (logChatId.Length == 0)
                return;

            var error = string.IsNullOrWhiteSpace(result.Error)
                ? "فشل تجهيز منتج نون للنشر."
                : result.Error.Trim();
            var route = string.IsNullOrWhiteSpace(result.Route) ? null : result.Route.Trim();
            var header = route is null
                ? $"{error}\nSKU: {sku}"
                : $"{error}\nSKU: {sku}\nRoute: {route}";
            var text = string.IsNullOrWhiteSpace(result.Detail)
                ? header
                : $"{header}\n\nLog:\n{result.Detail.Trim()}";
            if (text.Length > 3500)
                text = text[..3500] + "…";

            await telegram.SendPlainTextAsync(logChatId, text, replyToMessageId: null, ct);
        }

        private async Task SendPublishOptionsAsync(
            string chatId,
            string sku,
            int replyToMessageId,
            CancellationToken ct)
        {
            object Button(string label, string kind) => new[]
            {
                new Dictionary<string, object>
                {
                    ["text"] = label,
                    ["callback_data"] = $"{PublishCallbackPrefix}{kind}:{sku}"
                }
            };

            var replyMarkup = JsonSerializer.Serialize(new
            {
                inline_keyboard = new[]
                {
                    Button("نشر عرض", "offer"),
                    Button("نشر عرض ممتاز", "greatoffer"),
                    Button("نشر لقطة", "snap"),
                    Button("نشر ببلاش", "free")
                }
            });

            var token = options.Value.BotToken.Trim();
            var client = httpClientFactory.CreateClient(TelegramNotifier.HttpClientName);
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            var payload = new Dictionary<string, object?>
            {
                ["chat_id"] = chatId,
                ["text"] = HttpUtility.HtmlEncode($"اختر قالب النشر (نون {sku}):"),
                ["parse_mode"] = "HTML",
                ["reply_to_message_id"] = replyToMessageId,
                ["reply_markup"] = JsonSerializer.Deserialize<JsonElement>(replyMarkup)
            };
            using var response = await client.PostAsJsonAsync(url, payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Noon publish options failed: {Status} {Body}", (int)response.StatusCode, body);
            }
        }

        private async Task<int?> SendPreviewAsync(
            string chatId,
            string captionHtml,
            byte[] screenshotPng,
            string token,
            int? replyToMessageId,
            CancellationToken ct)
        {
            var previewCaption = TelegramNotifier.ClampCaptionHtml(
                captionHtml + "\n\n" + HttpUtility.HtmlEncode(TelegramNotifier.PublishEditHint));
            var png = ImageLogoOverlay.Apply(screenshotPng, cartScreenshot: false);

            var botToken = options.Value.BotToken.Trim();
            var client = httpClientFactory.CreateClient(TelegramNotifier.HttpClientName);
            var url = $"https://api.telegram.org/bot{botToken}/sendPhoto";

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(chatId), "chat_id");
            var photoContent = new ByteArrayContent(png);
            photoContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(photoContent, "photo", "noon-publish-preview.png");
            form.Add(new StringContent(previewCaption), "caption");
            form.Add(new StringContent("HTML"), "parse_mode");
            form.Add(new StringContent(BuildConfirmKeyboardJson(token)), "reply_markup");
            if (replyToMessageId is > 0)
                form.Add(new StringContent(replyToMessageId.Value.ToString()), "reply_to_message_id");

            using var response = await client.PostAsync(url, form, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Noon publish preview failed: {Status} {Body}", (int)response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                return null;
            if (doc.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("message_id", out var mid))
            {
                return mid.GetInt32();
            }

            return null;
        }

        private static string BuildConfirmKeyboardJson(string token)
        {
            object Row(string text, string prefix) => new[]
            {
                new Dictionary<string, object>
                {
                    ["text"] = text,
                    ["callback_data"] = prefix + token
                }
            };

            return JsonSerializer.Serialize(new
            {
                inline_keyboard = new[]
                {
                    Row("تأكيد مع صورة", PublishConfirmPrefix),
                    Row("تأكيد بدون صورة", PublishConfirmTextPrefix),
                    Row("إلغاء", PublishCancelPrefix)
                }
            });
        }

        public static string? ExtractSku(string text)
        {
            var urlMatch = NoonUrl.Match(text);
            if (urlMatch.Success &&
                NoonPrepareForPublishService.TryResolveSkuAndUrl(urlMatch.Value, out var skuFromUrl, out _))
            {
                return skuFromUrl;
            }

            var skuMatch = SkuToken.Match(text);
            return skuMatch.Success ? skuMatch.Groups[1].Value.ToUpperInvariant() : null;
        }
    }
}
