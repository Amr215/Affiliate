using Affiliate.Data;
using Affiliate.Options;
using Affiliate.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;
using System.Xml.Linq;

namespace Affiliate.Controllers
{
    [AllowAnonymous]
    public class SeoController : Controller
    {
        private readonly string _siteBase;
        private readonly AffiliateDbContext _db;

        public SeoController(IOptions<TelegramOptions> telegramOptions, AffiliateDbContext db)
        {
            _siteBase = (telegramOptions.Value.PublishLinkBaseUrl ?? "https://el-sindbad.com").Trim().TrimEnd('/');
            _db = db;
        }

        [HttpGet("/robots.txt")]
        [ResponseCache(Duration = 86400)]
        public ContentResult Robots()
        {
            var body =
                "User-agent: *\n" +
                "Allow: /\n" +
                "Allow: /tracker\n" +
                "Disallow: /Dashboard\n" +
                "Disallow: /Account\n" +
                "Disallow: /Products\n" +
                "Disallow: /ExternalPrices\n" +
                "Disallow: /SearchTags\n" +
                "Disallow: /Telegram\n" +
                "Disallow: /IspProxy\n" +
                "Disallow: /Noon\n" +
                "Disallow: /health\n" +
                "\n" +
                $"Sitemap: {_siteBase}/sitemap.xml\n";

            return Content(body, "text/plain", Encoding.UTF8);
        }

        [HttpGet("/sitemap.xml")]
        [ResponseCache(Duration = 3600)]
        public async Task<ContentResult> Sitemap(CancellationToken cancellationToken)
        {
            XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
            XNamespace xhtml = "http://www.w3.org/1999/xhtml";

            var staticUrls = new (string Path, string Changefreq, string Priority)[]
            {
                ("/?lang=ar", "hourly", "1.0"),
                ("/?lang=en", "hourly", "0.9"),
            };

            var products = await _db.Products.AsNoTracking()
                .Where(p => p.IsBlocked != true
                    && p.NotAvailableDate == null
                    && p.Asin != null
                    && p.CurrentPrice > 0
                    && p.ScraperUrl != null
                    && p.ScraperUrl.IsAsinRecheckEnabled
                    && p.ScraperUrl.TagId != null)
                .OrderByDescending(p => p.LastCheckedAt)
                .Take(5000)
                .Select(p => new { p.Asin, p.Name, p.NameAr })
                .ToListAsync(cancellationToken);

            var urlElements = new List<XElement>();

            foreach (var u in staticUrls)
            {
                var arPath = u.Path.Contains("lang=en", StringComparison.Ordinal)
                    ? u.Path.Replace("lang=en", "lang=ar", StringComparison.Ordinal)
                    : u.Path;
                var enPath = u.Path.Contains("lang=ar", StringComparison.Ordinal)
                    ? u.Path.Replace("lang=ar", "lang=en", StringComparison.Ordinal)
                    : u.Path;

                urlElements.Add(BuildUrl(ns, xhtml, u.Path, u.Changefreq, u.Priority, arPath, enPath));
            }

            foreach (var p in products)
            {
                var asin = AsinLinkCodec.NormalizeAsin(p.Asin!);
                if (!AsinLinkCodec.IsValidAsin(asin))
                    continue;

                var slugAr = PriceTrackerSlug.FromName(string.IsNullOrWhiteSpace(p.NameAr) ? p.Name : p.NameAr);
                var slugEn = PriceTrackerSlug.FromName(p.Name);
                var pathAr = $"/tracker/{asin}/{Uri.EscapeDataString(slugAr)}?lang=ar";
                var pathEn = $"/tracker/{asin}/{Uri.EscapeDataString(slugEn)}?lang=en";

                urlElements.Add(BuildUrl(ns, xhtml, pathAr, "daily", "0.7", pathAr, pathEn));
                urlElements.Add(BuildUrl(ns, xhtml, pathEn, "daily", "0.6", pathAr, pathEn));
            }

            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(ns + "urlset",
                    new XAttribute(XNamespace.Xmlns + "xhtml", xhtml),
                    urlElements));

            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + doc.ToString(SaveOptions.DisableFormatting);
            return Content(xml, "application/xml", Encoding.UTF8);
        }

        private XElement BuildUrl(
            XNamespace ns,
            XNamespace xhtml,
            string path,
            string changefreq,
            string priority,
            string arPath,
            string enPath) =>
            new(ns + "url",
                new XElement(ns + "loc", _siteBase + path),
                new XElement(ns + "changefreq", changefreq),
                new XElement(ns + "priority", priority),
                new XElement(xhtml + "link",
                    new XAttribute("rel", "alternate"),
                    new XAttribute("hreflang", "ar"),
                    new XAttribute("href", _siteBase + arPath)),
                new XElement(xhtml + "link",
                    new XAttribute("rel", "alternate"),
                    new XAttribute("hreflang", "en"),
                    new XAttribute("href", _siteBase + enPath)),
                new XElement(xhtml + "link",
                    new XAttribute("rel", "alternate"),
                    new XAttribute("hreflang", "x-default"),
                    new XAttribute("href", _siteBase + arPath)));
    }
}
