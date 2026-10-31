using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Affiliate.Noon
{
    public sealed class NoonPublishDraft
    {
        public required string Token { get; init; }
        public required string ChatId { get; init; }
        public required string Sku { get; init; }
        public required string CaptionHtml { get; set; }
        public required byte[] ScreenshotPng { get; init; }
        public Guid CorrelationId { get; init; }
        public int PreviewMessageId { get; set; }
        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    }

    /// <summary>Short-lived in-memory Noon publish previews (lost on restart).</summary>
    public sealed class NoonPublishDraftCache
    {
        public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
        private const int TokenLength = 8;
        private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        private readonly ConcurrentDictionary<string, NoonPublishDraft> _byToken = new();
        private readonly ConcurrentDictionary<string, string> _byPreview = new();

        public NoonPublishDraft Create(
            string chatId,
            string sku,
            string captionHtml,
            byte[] screenshotPng,
            Guid correlationId = default)
        {
            Prune();
            var token = NewToken();
            var draft = new NoonPublishDraft
            {
                Token = token,
                ChatId = chatId,
                Sku = sku,
                CaptionHtml = captionHtml,
                ScreenshotPng = screenshotPng,
                CorrelationId = correlationId == default ? Guid.NewGuid() : correlationId
            };
            _byToken[token] = draft;
            return draft;
        }

        public void SetPreviewMessageId(NoonPublishDraft draft, int messageId)
        {
            draft.PreviewMessageId = messageId;
            _byPreview[PreviewKey(draft.ChatId, messageId)] = draft.Token;
        }

        public bool TryGet(string token, out NoonPublishDraft? draft)
        {
            draft = null;
            if (string.IsNullOrWhiteSpace(token) || !_byToken.TryGetValue(token, out var found))
                return false;
            if (IsExpired(found))
            {
                Remove(found.Token);
                return false;
            }
            draft = found;
            return true;
        }

        public bool TryGetByPreviewMessage(string chatId, int messageId, out NoonPublishDraft? draft)
        {
            draft = null;
            if (!_byPreview.TryGetValue(PreviewKey(chatId, messageId), out var token))
                return false;
            return TryGet(token, out draft);
        }

        public void UpdateCaption(NoonPublishDraft draft, string captionHtml) =>
            draft.CaptionHtml = captionHtml;

        public void Remove(string token)
        {
            if (!_byToken.TryRemove(token, out var draft))
                return;
            if (draft.PreviewMessageId > 0)
                _byPreview.TryRemove(PreviewKey(draft.ChatId, draft.PreviewMessageId), out _);
        }

        private void Prune()
        {
            foreach (var kv in _byToken)
            {
                if (IsExpired(kv.Value))
                    Remove(kv.Key);
            }
        }

        private static bool IsExpired(NoonPublishDraft d) =>
            DateTime.UtcNow - d.CreatedAtUtc > Ttl;

        private static string PreviewKey(string chatId, int messageId) => $"{chatId}:{messageId}";

        private string NewToken()
        {
            Span<char> buf = stackalloc char[TokenLength];
            Span<byte> bytes = stackalloc byte[TokenLength];
            for (var attempt = 0; attempt < 16; attempt++)
            {
                RandomNumberGenerator.Fill(bytes);
                for (var i = 0; i < TokenLength; i++)
                    buf[i] = Alphabet[bytes[i] % Alphabet.Length];
                var token = new string(buf);
                if (!_byToken.ContainsKey(token))
                    return token;
            }
            return Guid.NewGuid().ToString("N")[..TokenLength];
        }
    }
}
