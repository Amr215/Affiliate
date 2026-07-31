using System.Globalization;
using System.Text.RegularExpressions;
using Affiliate.Models.Dtos;
using HtmlAgilityPack;

namespace Affiliate.Services
{
    /// <summary>Parses Amazon search-results HTML into <see cref="OrganicProduct"/> rows.</summary>
    public static class AmazonSearchHtmlParser
    {
        private static readonly Regex RatingText = new(
            @"([\d.]+)\s+out\s+of\s+5",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Digits = new(
            @"[\d,]+",
            RegexOptions.Compiled);

        public static SearchPageParseResult Parse(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html ?? string.Empty);

            var organic = new List<OrganicProduct>();
            var nodes = doc.DocumentNode.SelectNodes(
                "//div[@data-component-type='s-search-result' and @data-asin]");

            if (nodes is null)
                return new SearchPageParseResult(organic, DetectLastPage(doc));

            var position = 0;
            foreach (var node in nodes)
            {
                var asin = node.GetAttributeValue("data-asin", null)?.Trim();
                if (string.IsNullOrWhiteSpace(asin) || asin.Length != 10)
                    continue;

                // Skip empty placeholder slots
                if (string.IsNullOrWhiteSpace(node.InnerText))
                    continue;

                position++;
                organic.Add(ParseCard(node, asin, position));
            }

            return new SearchPageParseResult(organic, DetectLastPage(doc));
        }

        private static OrganicProduct ParseCard(HtmlNode node, string asin, int position)
        {
            var titleNode = node.SelectSingleNode(".//h2//span")
                ?? node.SelectSingleNode(".//h2");
            var title = HtmlEntity.DeEntitize(titleNode?.InnerText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title))
                title = node.SelectSingleNode(".//h2")?.GetAttributeValue("aria-label", null)?.Trim()
                    ?? string.Empty;

            var link = node.SelectSingleNode(".//a[contains(@href,'/dp/')]")
                ?.GetAttributeValue("href", null);
            if (string.IsNullOrWhiteSpace(link))
                link = $"/dp/{asin}";

            var image = node.SelectSingleNode(".//img[contains(@class,'s-image')]")
                ?.GetAttributeValue("src", null);

            var (price, currency) = ParsePrice(node);
            var rating = ParseRating(node);
            var reviews = ParseReviews(node);

            var isSponsored = node.GetAttributeValue("class", "").Contains("AdHolder", StringComparison.Ordinal)
                || node.SelectSingleNode(".//*[contains(@class,'puis-sponsored-label-text')]") is not null
                || node.InnerText.Contains("Sponsored", StringComparison.OrdinalIgnoreCase);

            var isBestSeller = node.SelectSingleNode(".//*[@id='BEST_SELLER' or contains(@data-csa-c-content-id,'BEST_SELLER')]") is not null
                || node.InnerText.Contains("Best Seller", StringComparison.OrdinalIgnoreCase);

            var isPrime = node.SelectSingleNode(".//i[contains(@class,'a-icon-prime')]") is not null
                || node.SelectSingleNode(".//*[@aria-label='Amazon Prime']") is not null;

            var shipping = node.SelectSingleNode(
                    ".//*[contains(@data-cy,'delivery-recipe') or contains(@class,'udm-primary-delivery-message')]")
                ?.InnerText;
            shipping = string.IsNullOrWhiteSpace(shipping)
                ? null
                : HtmlEntity.DeEntitize(shipping).Trim();

            return new OrganicProduct
            {
                Asin = asin,
                Title = title,
                Url = link ?? $"/dp/{asin}",
                Price = price,
                Currency = currency ?? string.Empty,
                Rating = rating,
                ReviewsCount = reviews,
                ImageUrl = image ?? string.Empty,
                Position = position,
                IsSponsored = isSponsored,
                BestSeller = isBestSeller,
                IsPrime = isPrime,
                ShippingInformation = shipping ?? string.Empty,
                Manufacturer = GuessManufacturer(title) ?? string.Empty
            };
        }

        private static (decimal? Price, string? Currency) ParsePrice(HtmlNode node)
        {
            var offscreen = node.SelectSingleNode(".//span[contains(@class,'a-price')]//span[contains(@class,'a-offscreen')]")
                ?.InnerText?.Trim();
            if (!string.IsNullOrWhiteSpace(offscreen))
                return ParsePriceText(HtmlEntity.DeEntitize(offscreen));

            var whole = node.SelectSingleNode(".//span[contains(@class,'a-price-whole')]")?.InnerText;
            var frac = node.SelectSingleNode(".//span[contains(@class,'a-price-fraction')]")?.InnerText;
            if (!string.IsNullOrWhiteSpace(whole))
            {
                var symbol = node.SelectSingleNode(".//span[contains(@class,'a-price-symbol')]")?.InnerText?.Trim();
                var text = $"{symbol}{whole}{frac}".Trim();
                return ParsePriceText(HtmlEntity.DeEntitize(text));
            }

            return (null, null);
        }

        private static (decimal? Price, string? Currency) ParsePriceText(string text)
        {
            text = text.Replace('\u00a0', ' ').Trim();
            string? currency = null;
            if (text.Contains("EGP", StringComparison.OrdinalIgnoreCase))
                currency = "EGP";
            else if (text.Contains('$'))
                currency = "USD";
            else if (text.Contains('€'))
                currency = "EUR";
            else if (text.Contains('£'))
                currency = "GBP";

            var cleaned = Regex.Replace(text, @"[^\d.,]", "");
            if (string.IsNullOrWhiteSpace(cleaned))
                return (null, currency);

            // Amazon.eg uses thousands separators as commas: 17,192.47
            if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
            {
                cleaned = cleaned.Replace(",", "");
                if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out price))
                    return (null, currency);
            }

            return (price, currency);
        }

        private static double? ParseRating(HtmlNode node)
        {
            var alt = node.SelectSingleNode(".//span[contains(@class,'a-icon-alt')]")?.InnerText
                ?? node.SelectSingleNode(".//*[@aria-label and contains(@aria-label,'out of 5')]")
                    ?.GetAttributeValue("aria-label", null);

            if (string.IsNullOrWhiteSpace(alt))
                return null;

            var m = RatingText.Match(alt);
            if (m.Success &&
                double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating))
                return rating;

            return null;
        }

        private static int? ParseReviews(HtmlNode node)
        {
            var label = node.SelectSingleNode(
                    ".//a[contains(@aria-label,'rating') or contains(@aria-label,'review')]")
                ?.GetAttributeValue("aria-label", null)
                ?? node.SelectSingleNode(".//a[contains(@href,'#customerReviews')]//span")?.InnerText;

            if (string.IsNullOrWhiteSpace(label))
                return null;

            var m = Digits.Match(label);
            if (!m.Success)
                return null;

            if (int.TryParse(m.Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                return count;

            return null;
        }

        private static int? DetectLastPage(HtmlDocument doc)
        {
            var pageLinks = doc.DocumentNode.SelectNodes(
                "//*[contains(@class,'s-pagination-item') and not(contains(@class,'s-pagination-previous')) and not(contains(@class,'s-pagination-next'))]");

            if (pageLinks is null || pageLinks.Count == 0)
                return 1;

            var max = 1;
            foreach (var link in pageLinks)
            {
                var text = HtmlEntity.DeEntitize(link.InnerText).Trim();
                if (int.TryParse(text, out var page) && page > max)
                    max = page;

                var aria = link.GetAttributeValue("aria-label", null);
                if (!string.IsNullOrWhiteSpace(aria))
                {
                    var m = Digits.Match(aria);
                    if (m.Success &&
                        int.TryParse(m.Value.Replace(",", ""), out var fromAria) &&
                        fromAria > max)
                        max = fromAria;
                }
            }

            return max;
        }

        private static string? GuessManufacturer(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return null;
            var first = title.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            return first.Length > 0 ? first[0] : null;
        }
    }

    public sealed record SearchPageParseResult(List<OrganicProduct> Organic, int? LastVisiblePage);
}
