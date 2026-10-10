using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Affiliate.Noon
{
    /// <summary>
    /// Reads the catalog data that Noon's listing page embeds as a serialized JavaScript object
    /// (<c>nbHits:14,nbPages:1,...,hits:$R[1069]=[$R[1070]={offer_code:"...",sku:"...",price:6699,...}]</c>).
    /// The fields match the catalog API, but the format is JavaScript rather than JSON, so it is read field by field.
    /// </summary>
    public static class NoonHtmlParser
    {
        /// <summary>Upper bound for one hit's text; hits are far smaller, this only stops the last one running to the end of the page.</summary>
        private const int MaxHitLength = 8000;

        private static readonly Regex NbHits = new(@"\bnbHits:(\d+)", RegexOptions.Compiled);
        private static readonly Regex NbPages = new(@"\bnbPages:(\d+)", RegexOptions.Compiled);
        private static readonly Regex HitStart = new(@"=\{offer_code:""", RegexOptions.Compiled);
        private static readonly Regex Sku = Field(@"\bsku:""([^""]+)""");
        private static readonly Regex Name = Field(@"\bname:""((?:[^""\\]|\\.)*)""");
        private static readonly Regex Brand = Field(@"\bbrand:""((?:[^""\\]|\\.)*)""");
        private static readonly Regex BrandCode = Field(@"\bbrand_code:""([^""]*)""");
        // "null" must match too, or a missing sale price picks up a later object's sale_price.
        private static readonly Regex Price = Field(@"\bprice:([\d.]+|null)");
        private static readonly Regex SalePrice = Field(@"\bsale_price:([\d.]+|null)");
        private static readonly Regex PdpUrl = Field(@"\bpdp_url:""([^""]+)""");
        private static readonly Regex ImageUrl = Field(@"\bimage_url:""([^""]+)""");
        private static readonly Regex StoreName = Field(@"\bstore_name:""((?:[^""\\]|\\.)*)""");
        private static readonly Regex IsBestSeller = Field(@"\bis_bestseller:!0");
        private static readonly Regex RatingValue = Field(@"product_rating:\$R\[\d+\]=\{[^}]*?\bvalue:([\d.]+)");
        private static readonly Regex RatingCount = Field(@"product_rating:\$R\[\d+\]=\{[^}]*?\bcount:(\d+)");

        /// <summary>Null when the page has no catalog data (e.g. a block or captcha page).</summary>
        public static NoonCatalogResponse? Parse(string html)
        {
            var nbPages = NbPages.Match(html);
            var hitsStart = html.IndexOf("hits:$R[", StringComparison.Ordinal);
            if (!nbPages.Success || hitsStart < 0)
                return null;

            var starts = HitStart.Matches(html, hitsStart).Select(m => m.Index + m.Length).ToList();
            var hits = new List<NoonHit>(starts.Count);
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1] : html.Length;
                hits.Add(ParseHit(html.Substring(starts[i], Math.Min(end - starts[i], MaxHitLength))));
            }

            var nbHits = NbHits.Match(html);
            return new NoonCatalogResponse
            {
                NbHits = nbHits.Success ? int.Parse(nbHits.Groups[1].Value) : hits.Count,
                NbPages = int.Parse(nbPages.Groups[1].Value),
                Hits = hits
            };
        }

        private static NoonHit ParseHit(string text) => new()
        {
            Sku = Text(Sku, text),
            Name = Text(Name, text),
            Brand = Text(Brand, text),
            BrandCode = Text(BrandCode, text),
            Price = Number(Price, text),
            SalePrice = Number(SalePrice, text),
            PdpUrl = Text(PdpUrl, text),
            ImageUrl = Text(ImageUrl, text),
            StoreName = Text(StoreName, text),
            IsBestSeller = IsBestSeller.IsMatch(text),
            ProductRating = new NoonRating
            {
                Value = (double?)Number(RatingValue, text),
                Count = (int?)Number(RatingCount, text)
            }
        };

        private static Regex Field(string pattern) => new(pattern, RegexOptions.Compiled);

        /// <summary>First match in the hit, with JavaScript string escapes (\" \u0026 ...) decoded.</summary>
        private static string? Text(Regex regex, string text)
        {
            var match = regex.Match(text);
            if (!match.Success)
                return null;

            try
            {
                return JsonSerializer.Deserialize<string>($"\"{match.Groups[1].Value}\"");
            }
            catch (JsonException)
            {
                return match.Groups[1].Value;
            }
        }

        private static decimal? Number(Regex regex, string text)
        {
            var match = regex.Match(text);
            return match.Success && decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
    }
}
