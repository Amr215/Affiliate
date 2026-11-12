using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Affiliate.Services
{
    /// <summary>
    /// Extracts a plain-text product description from an Amazon PDP HTML body
    /// (feature bullets, product description, or meta description).
    /// </summary>
    public static class AmazonProductDescriptionParser
    {
        private static readonly Regex WhitespaceCollapse = new(@"\s+", RegexOptions.Compiled);

        public static string? Parse(string html, int maxLength = 4000)
        {
            if (string.IsNullOrWhiteSpace(html))
                return null;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var bullets = ExtractFeatureBullets(doc);
            if (bullets.Count > 0)
                return Clamp(JoinBullets(bullets), maxLength);

            var productDesc = ExtractProductDescription(doc);
            if (!string.IsNullOrWhiteSpace(productDesc))
                return Clamp(productDesc, maxLength);

            var meta = ExtractMetaDescription(doc);
            if (!string.IsNullOrWhiteSpace(meta))
                return Clamp(meta, maxLength);

            return null;
        }

        private static List<string> ExtractFeatureBullets(HtmlDocument doc)
        {
            var result = new List<string>();
            var nodes = doc.DocumentNode.SelectNodes(
                "//div[@id='feature-bullets']//li//span[contains(@class,'a-list-item')]"
                + " | //div[@id='featurebullets_feature_div']//li//span[contains(@class,'a-list-item')]"
                + " | //div[@id='feature-bullets']//li");

            if (nodes is null)
                return result;

            foreach (var node in nodes)
            {
                var text = NormalizeText(node.InnerText);
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (IsNoiseBullet(text))
                    continue;
                if (result.Exists(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase)))
                    continue;
                result.Add(text);
            }

            return result;
        }

        private static string? ExtractProductDescription(HtmlDocument doc)
        {
            var node = doc.DocumentNode.SelectSingleNode("//div[@id='productDescription']")
                ?? doc.DocumentNode.SelectSingleNode("//div[@id='productDescription_feature_div']")
                ?? doc.DocumentNode.SelectSingleNode("//div[@id='bookDescription_feature_div']");

            if (node is null)
                return null;

            // Prefer paragraphs; fall back to full inner text.
            var paragraphs = node.SelectNodes(".//p");
            if (paragraphs is { Count: > 0 })
            {
                var parts = paragraphs
                    .Select(p => NormalizeText(p.InnerText))
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();
                if (parts.Count > 0)
                    return string.Join(" ", parts);
            }

            return NormalizeText(node.InnerText);
        }

        private static string? ExtractMetaDescription(HtmlDocument doc)
        {
            var meta = doc.DocumentNode.SelectSingleNode("//meta[@name='description']")
                ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']");
            var content = meta?.GetAttributeValue("content", null);
            return NormalizeText(content);
        }

        private static string JoinBullets(IReadOnlyList<string> bullets)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < bullets.Count; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                sb.Append(bullets[i].TrimEnd('.', '۔', '。'));
                sb.Append('.');
            }

            return sb.ToString();
        }

        private static bool IsNoiseBullet(string text)
        {
            if (text.Length < 8)
                return true;

            return text.Contains("Make sure this fits", StringComparison.OrdinalIgnoreCase)
                || text.Contains("by entering your model number", StringComparison.OrdinalIgnoreCase)
                || (text.Contains("اضغط على", StringComparison.OrdinalIgnoreCase)
                    && text.Contains("للتأكد", StringComparison.OrdinalIgnoreCase))
                || text.Contains("See more product details", StringComparison.OrdinalIgnoreCase)
                || text.Contains("عرض المزيد", StringComparison.OrdinalIgnoreCase);
        }

        private static string? NormalizeText(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var decoded = WebUtility.HtmlDecode(raw);
            decoded = WhitespaceCollapse.Replace(decoded, " ").Trim();
            return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
        }

        private static string Clamp(string text, int maxLength)
        {
            if (maxLength <= 0 || text.Length <= maxLength)
                return text;

            var cut = text[..maxLength].TrimEnd();
            var lastSpace = cut.LastIndexOf(' ');
            if (lastSpace > maxLength * 3 / 4)
                cut = cut[..lastSpace];
            return cut.TrimEnd(',', '.', ';', '،') + "…";
        }
    }
}
