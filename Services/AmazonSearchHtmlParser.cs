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

        public static SearchPageParseResult Parse(string html) => Parse(html, sourcePageUrl: null);

        /// <param name="sourcePageUrl">
        /// When set to an Amazon Resale / department-browse URL, cards that lack
        /// <c>a-price</c> markup fall back to <c>data-csa-c-price-to-pay</c>.
        /// </param>
        public static SearchPageParseResult Parse(string html, string? sourcePageUrl)
        {
            var resalePriceFallback = IsResaleBrowseUrl(sourcePageUrl);
            var doc = new HtmlDocument();
            doc.LoadHtml(html ?? string.Empty);

            var organic = new List<OrganicProduct>();
            var nodes = doc.DocumentNode.SelectNodes(
                "//div[@data-component-type='s-search-result' and @data-asin]")
                ?? doc.DocumentNode.SelectNodes(
                    "//div[contains(@class,'s-main-slot')]//div[@data-asin and string-length(@data-asin)=10]");

            if (nodes is null)
                return new SearchPageParseResult(organic, DetectLastPage(doc), DetectNextPageHref(doc));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var position = 0;
            foreach (var node in nodes)
            {
                var asin = node.GetAttributeValue("data-asin", null)?.Trim();
                if (string.IsNullOrWhiteSpace(asin) || asin.Length != 10)
                    continue;

                if (!seen.Add(asin))
                {
                    // Resale pages list the same ASIN as a priceless sponsored ad and again as an organic card.
                    if (resalePriceFallback)
                    {
                        var index = organic.FindIndex(o => string.Equals(o.Asin, asin, StringComparison.OrdinalIgnoreCase));
                        if (index >= 0 && organic[index].Price is null)
                        {
                            var replacement = ParseCard(node, asin, organic[index].Position, resalePriceFallback);
                            if (replacement.Price is not null)
                                organic[index] = replacement;
                        }
                    }
                    continue;
                }

                // Skip empty placeholder slots
                if (string.IsNullOrWhiteSpace(node.InnerText))
                    continue;

                position++;
                organic.Add(ParseCard(node, asin, position, resalePriceFallback));
            }

            return new SearchPageParseResult(organic, DetectLastPage(doc), DetectNextPageHref(doc));
        }

        /// <summary>
        /// Department / Amazon Resale browse pages (<c>bbn=</c>, warehouse) often expose
        /// prices only via CSA attributes on result cards, not via <c>a-offscreen</c>.
        /// </summary>
        internal static bool IsResaleBrowseUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)
                || url.IndexOf("amazon.", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            if (url.Contains("p_n_g-101014971069111", StringComparison.Ordinal)
                || url.Contains("i=warehouse", StringComparison.OrdinalIgnoreCase)
                || url.Contains("warehouse-deals", StringComparison.OrdinalIgnoreCase)
                || url.Contains("p_n_condition_type", StringComparison.OrdinalIgnoreCase))
                return true;

            return url.Contains("bbn=", StringComparison.OrdinalIgnoreCase)
                && !url.Contains("k=", StringComparison.OrdinalIgnoreCase);
        }

        private static OrganicProduct ParseCard(HtmlNode node, string asin, int position, bool resalePriceFallback)
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

            var (price, currency) = ParsePrice(node, resalePriceFallback);
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

        private static (decimal? Price, string? Currency) ParsePrice(HtmlNode node, bool resalePriceFallback)
        {
            var direct = ParsePriceFromVisibleMarkup(node);
            if (direct.Price is not null)
                return direct;

            return resalePriceFallback ? ParseResalePriceFallback(node) : (null, null);
        }

        private static (decimal? Price, string? Currency) ParsePriceFromVisibleMarkup(HtmlNode node)
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

        /// <summary>
        /// Resale cards have no main <c>a-price</c>; the Amazon Resale offer is under
        /// "More Buying Choices" (<c>data-cy="secondary-offer-recipe"</c>), e.g. <c>EGP 1,047.13</c>.
        /// </summary>
        private static (decimal? Price, string? Currency) ParseResalePriceFallback(HtmlNode node)
        {
            var secondary = node.SelectSingleNode(
                ".//*[@data-cy='secondary-offer-recipe']//span[contains(@class,'a-color-base')]");
            if (secondary is not null)
            {
                var text = HtmlEntity.DeEntitize(secondary.InnerText ?? string.Empty);
                if (TryParseMoneyAmount(text, out var offer) && offer > 0)
                    return (offer, "EGP");
            }

            var csaNodes = node.SelectNodes(".//*[@data-csa-c-price-to-pay]");
            if (csaNodes is not null)
            {
                foreach (var csa in csaNodes)
                {
                    var raw = csa.GetAttributeValue("data-csa-c-price-to-pay", null);
                    var parsed = ParseCsaPriceToPay(raw);
                    if (parsed is > 0)
                        return (parsed, "EGP");
                }
            }

            var priceToPay = node.SelectSingleNode(".//span[contains(@class,'priceToPay')]");
            if (priceToPay is not null)
            {
                var whole = priceToPay.SelectSingleNode(".//span[contains(@class,'a-price-whole')]")?.InnerText;
                var frac = priceToPay.SelectSingleNode(".//span[contains(@class,'a-price-fraction')]")?.InnerText ?? "00";
                if (!string.IsNullOrWhiteSpace(whole))
                {
                    var text = Regex.Replace($"{whole}.{frac}", @"[^\d.,]", "");
                    if (TryParseMoneyAmount(text, out var fromBlock) && fromBlock > 0)
                        return (fromBlock, "EGP");
                }
            }

            return (null, null);
        }

        private static decimal? ParseCsaPriceToPay(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return TryParseMoneyAmount(raw, out var value) && value > 0 ? value : null;
        }

        private static bool TryParseMoneyAmount(string text, out decimal value)
        {
            value = 0;
            text = text.Replace('\u00a0', ' ').Trim();
            var match = Regex.Match(text, @"[\d][\d.,]*");
            if (!match.Success)
                return false;

            var cleaned = Regex.Replace(match.Value, @"\.+", ".").Trim('.');
            if (string.IsNullOrWhiteSpace(cleaned))
                return false;

            if (cleaned.Contains(',') && cleaned.Contains('.'))
            {
                if (cleaned.LastIndexOf(',') > cleaned.LastIndexOf('.'))
                    cleaned = cleaned.Replace(".", "").Replace(',', '.');
                else
                    cleaned = cleaned.Replace(",", "");
            }
            else if (cleaned.Contains(','))
            {
                var parts = cleaned.Split(',');
                cleaned = parts.Length == 2 && parts[1].Length <= 2
                    ? cleaned.Replace(',', '.')
                    : cleaned.Replace(",", "");
            }

            return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
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
            var reviewsLink = node.SelectSingleNode(".//a[contains(@href,'#customerReviews')]");
            var label = reviewsLink?.GetAttributeValue("aria-label", null)
                ?? reviewsLink?.SelectSingleNode(".//span")?.InnerText
                ?? node.SelectSingleNode(
                        ".//a[contains(@aria-label,'rating') or contains(@aria-label,'review')]")
                    ?.GetAttributeValue("aria-label", null);

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

        /// <summary>
        /// Relative or absolute href from the enabled Next control. Amazon pagination requires
        /// tokens from this URL (<c>qid</c>, <c>xpid</c>, …); constructing <c>page=N</c> alone returns page 1 again.
        /// </summary>
        private static string? DetectNextPageHref(HtmlDocument doc)
        {
            var next = doc.DocumentNode.SelectSingleNode(
                "//a[contains(@class,'s-pagination-next') and not(contains(@class,'s-pagination-disabled'))]");
            if (next is null)
                return null;

            var ariaDisabled = next.GetAttributeValue("aria-disabled", null);
            if (string.Equals(ariaDisabled, "true", StringComparison.OrdinalIgnoreCase))
                return null;

            var href = next.GetAttributeValue("href", null);
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#') ||
                href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                return null;

            return HtmlEntity.DeEntitize(href).Trim();
        }

        private static string? GuessManufacturer(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return null;
            var first = title.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            return first.Length > 0 ? first[0] : null;
        }
    }

    public sealed record SearchPageParseResult(
        List<OrganicProduct> Organic,
        int? LastVisiblePage,
        string? NextPageHref)
    {
        public bool HasNextPage => !string.IsNullOrWhiteSpace(NextPageHref);
    };
}
