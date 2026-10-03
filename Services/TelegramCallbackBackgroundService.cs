using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Affiliate.Data;
using Affiliate.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    /// <summary>
    /// Long-polls Telegram getUpdates: handles «تجهيز للنشر» callbacks, and Amazon links
    /// posted in the publish group (reply with template buttons, then publish on click).
    /// </summary>
    public sealed class TelegramCallbackBackgroundService : BackgroundService
    {
        public const string CallbackPrefix = "prep:";
        public const string PublishCallbackPrefix = "pub:";

        private static readonly Regex AsinInUrl = new(
            @"amazon\.[^\s]*?/(?:dp|gp/product|gp/aw/d)/([A-Z0-9]{10})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptions<TelegramOptions> _options;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TelegramCallbackBackgroundService> _logger;

        private long _offset;

        public TelegramCallbackBackgroundService(
            IServiceScopeFactory scopeFactory,
            IOptions<TelegramOptions> options,
            IHttpClientFactory httpClientFactory,
            ILogger<TelegramCallbackBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var opts = _options.Value;
            if (!opts.Enabled || !opts.PollUpdates || string.IsNullOrWhiteSpace(opts.BotToken))
            {
                _logger.LogInformation(
                    "Telegram callback polling disabled (Enabled={Enabled}, PollUpdates={Poll}, HasToken={HasToken})",
                    opts.Enabled, opts.PollUpdates, !string.IsNullOrWhiteSpace(opts.BotToken));
                return;
            }

            // Drop a conflicting webhook so getUpdates works.
            await TryDeleteWebhookAsync(opts.BotToken.Trim(), stoppingToken);

            _logger.LogInformation("Telegram callback polling started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var updates = await GetUpdatesAsync(opts.BotToken.Trim(), stoppingToken);
                    foreach (var update in updates)
                    {
                        _offset = update.UpdateId + 1;
                        if (update.Message is not null)
                            await HandleMessageAsync(update.Message, stoppingToken);

                        if (update.CallbackQuery is null)
                            continue;

                        _ = Task.Run(
                            () => HandleCallbackSafeAsync(update.CallbackQuery, stoppingToken),
                            CancellationToken.None);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Telegram getUpdates loop error; retrying");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }

        private async Task HandleCallbackSafeAsync(TelegramCallbackQuery callback, CancellationToken ct)
        {
            try
            {
                await HandleCallbackAsync(callback, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram prep callback failed: {Data}", callback.Data);
            }
        }

        private async Task HandleMessageAsync(TelegramCallbackMessage message, CancellationToken ct)
        {
            var chatId = message.Chat?.Id?.ToString();
            if (chatId is null || chatId != _options.Value.PublishChatId.Trim())
                return;

            var match = AsinInUrl.Match(message.Text ?? "");
            if (!match.Success)
                return;

            using var scope = _scopeFactory.CreateScope();
            var telegram = scope.ServiceProvider.GetRequiredService<ITelegramNotifier>();
            await telegram.SendPublishOptionsAsync(chatId, match.Groups[1].Value.ToUpperInvariant(), message.MessageId, ct);
        }

        private async Task HandlePublishCallbackAsync(TelegramCallbackQuery callback, string data, CancellationToken ct)
        {
            // pub:kind:ASIN
            var parts = data.Split(':');
            var chatId = callback.Message?.Chat?.Id?.ToString();
            if (parts.Length != 3 || string.IsNullOrWhiteSpace(chatId))
            {
                await AnswerCallbackAsync(callback.Id, "بيانات غير صالحة", showAlert: true, ct);
                return;
            }

            var (kind, asin) = (parts[1], parts[2]);
            await AnswerCallbackAsync(callback.Id, "جاري التجهيز للنشر…", showAlert: false, ct);

            using var scope = _scopeFactory.CreateScope();
            var prepare = scope.ServiceProvider.GetRequiredService<IPrepareForPublishService>();
            var telegram = scope.ServiceProvider.GetRequiredService<ITelegramNotifier>();
            var db = scope.ServiceProvider.GetRequiredService<AffiliateDbContext>();

            var knownPrice = await db.Products
                .Where(p => p.Asin == asin)
                .Select(p => p.CurrentPrice)
                .FirstOrDefaultAsync(ct);

            var result = await prepare.PrepareAsync(asin, knownPrice, ct);
            if (!result.Success || result.ScreenshotPng is null)
            {
                await telegram.SendPlainTextAsync(
                    chatId,
                    result.Error ?? "فشل تجهيز المنتج للنشر.",
                    callback.Message?.MessageId,
                    ct);
                return;
            }

            var productName = result.ProductName ?? asin;
            var productUrl = result.ProductUrl ?? PrepareForPublishService.BuildProductUrl(asin);

            await telegram.SendPublishPostAsync(
                chatId, kind, productName, productUrl, result.ScreenshotPng, callback.Message?.MessageId, ct);

            var channelId = _options.Value.PublishChannelId.Trim();
            if (channelId.Length > 0)
                await telegram.SendPublishPostAsync(
                    channelId, kind, productName, productUrl, result.ScreenshotPng, cancellationToken: ct);
        }

        private async Task HandleCallbackAsync(TelegramCallbackQuery callback, CancellationToken ct)
        {
            var data = callback.Data?.Trim() ?? "";
            if (data.StartsWith(PublishCallbackPrefix, StringComparison.OrdinalIgnoreCase))
            {
                await HandlePublishCallbackAsync(callback, data, ct);
                return;
            }

            if (!data.StartsWith(CallbackPrefix, StringComparison.OrdinalIgnoreCase))
                return;

            if (!TryParsePrepCallback(data, out var asin, out var alertPrice))
            {
                await AnswerCallbackAsync(callback.Id, "بيانات غير صالحة", showAlert: true, ct);
                return;
            }

            var chatId = callback.Message?.Chat?.Id?.ToString();
            if (string.IsNullOrWhiteSpace(chatId))
            {
                await AnswerCallbackAsync(callback.Id, "لا يمكن تحديد المحادثة", showAlert: true, ct);
                return;
            }

            await AnswerCallbackAsync(callback.Id, "جاري التجهيز للنشر…", showAlert: false, ct);

            using var scope = _scopeFactory.CreateScope();
            var prepare = scope.ServiceProvider.GetRequiredService<IPrepareForPublishService>();
            var telegram = scope.ServiceProvider.GetRequiredService<ITelegramNotifier>();

            var result = await prepare.PrepareAsync(asin, alertPrice, ct);
            if (!result.Success || result.ScreenshotPng is null)
            {
                await telegram.SendPlainTextAsync(
                    chatId,
                    result.Error ?? "فشل تجهيز المنتج للنشر.",
                    callback.Message?.MessageId,
                    ct);
                return;
            }

            await telegram.SendPreparePublishAsync(
                chatId,
                result.ProductName ?? asin,
                result.ProductUrl ?? PrepareForPublishService.BuildProductUrl(asin),
                result.ScreenshotPng,
                callback.Message?.MessageId,
                ct);
        }

        internal static bool TryParsePrepCallback(string data, out string asin, out decimal? alertPrice)
        {
            asin = "";
            alertPrice = null;

            // prep:ASIN or prep:ASIN:1234.56
            var payload = data[CallbackPrefix.Length..];
            var parts = payload.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
                return false;

            asin = parts[0].Trim().ToUpperInvariant();
            if (asin.Length is < 8 or > 16)
                return false;

            if (parts.Length == 2 &&
                decimal.TryParse(parts[1], System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var price))
            {
                alertPrice = price;
            }

            return true;
        }

        private async Task TryDeleteWebhookAsync(string botToken, CancellationToken ct)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(TelegramNotifier.HttpClientName);
                var url = $"https://api.telegram.org/bot{botToken}/deleteWebhook?drop_pending_updates=false";
                using var response = await client.GetAsync(url, ct);
                _logger.LogDebug("Telegram deleteWebhook status {Status}", (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Telegram deleteWebhook failed (ignored)");
            }
        }

        private async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(string botToken, CancellationToken ct)
        {
            var client = _httpClientFactory.CreateClient(TelegramNotifier.HttpClientName);
            // Long poll — allow up to ~35s (client timeout is 30s on TelegramBot; raise via query 25).
            var url =
                $"https://api.telegram.org/bot{botToken}/getUpdates?timeout=25&offset={_offset}" +
                "&allowed_updates=%5B%22callback_query%22%2C%22message%22%5D";

            using var response = await client.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Telegram getUpdates HTTP {Status}: {Body}", (int)response.StatusCode, body);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                return [];
            }

            var parsed = JsonSerializer.Deserialize<TelegramGetUpdatesResponse>(body);
            if (parsed is not { Ok: true })
            {
                _logger.LogWarning("Telegram getUpdates ok=false: {Body}", body);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                return [];
            }

            return parsed.Result ?? [];
        }

        private async Task AnswerCallbackAsync(
            string callbackQueryId,
            string text,
            bool showAlert,
            CancellationToken ct)
        {
            var token = _options.Value.BotToken.Trim();
            var client = _httpClientFactory.CreateClient(TelegramNotifier.HttpClientName);
            var url = $"https://api.telegram.org/bot{token}/answerCallbackQuery";
            var payload = new Dictionary<string, object?>
            {
                ["callback_query_id"] = callbackQueryId,
                ["text"] = text,
                ["show_alert"] = showAlert
            };
            using var response = await client.PostAsJsonAsync(url, payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogDebug("answerCallbackQuery failed: {Status} {Body}", (int)response.StatusCode, body);
            }
        }

        private sealed class TelegramGetUpdatesResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("ok")]
            public bool Ok { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("result")]
            public List<TelegramUpdate>? Result { get; set; }
        }

        private sealed class TelegramUpdate
        {
            [System.Text.Json.Serialization.JsonPropertyName("update_id")]
            public long UpdateId { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("callback_query")]
            public TelegramCallbackQuery? CallbackQuery { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("message")]
            public TelegramCallbackMessage? Message { get; set; }
        }

        private sealed class TelegramCallbackQuery
        {
            [System.Text.Json.Serialization.JsonPropertyName("id")]
            public string Id { get; set; } = "";

            [System.Text.Json.Serialization.JsonPropertyName("data")]
            public string? Data { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("message")]
            public TelegramCallbackMessage? Message { get; set; }
        }

        private sealed class TelegramCallbackMessage
        {
            [System.Text.Json.Serialization.JsonPropertyName("message_id")]
            public int MessageId { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("chat")]
            public TelegramChat? Chat { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("text")]
            public string? Text { get; set; }
        }

        private sealed class TelegramChat
        {
            [System.Text.Json.Serialization.JsonPropertyName("id")]
            public long? Id { get; set; }
        }
    }
}
