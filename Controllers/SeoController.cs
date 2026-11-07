using Affiliate.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text;
using System.Xml.Linq;

namespace Affiliate.Controllers
{
    [AllowAnonymous]
    public class SeoController : Controller
    {
        private readonly string _siteBase;

        public SeoController(IOptions<TelegramOptions> telegramOptions)
        {
            _siteBase = (telegramOptions.Value.PublishLinkBaseUrl ?? "https://el-sindbad.com").Trim().TrimEnd('/');
        }

        [HttpGet("/robots.txt")]
        [ResponseCache(Duration = 86400)]
        public ContentResult Robots()
        {
            var body =
                "User-agent: *\n" +
                "Allow: /\n" +
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
        public ContentResult Sitemap()
        {
            XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
            XNamespace xhtml = "http://www.w3.org/1999/xhtml";

            var urls = new (string Path, string Changefreq, string Priority)[]
            {
                ("/?lang=ar", "hourly", "1.0"),
                ("/?lang=en", "hourly", "0.9"),
                ("/Home/About?lang=ar", "monthly", "0.5"),
                ("/Home/About?lang=en", "monthly", "0.4"),
                ("/Home/Terms?lang=ar", "monthly", "0.5"),
                ("/Home/Terms?lang=en", "monthly", "0.4"),
            };

            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(ns + "urlset",
                    new XAttribute(XNamespace.Xmlns + "xhtml", xhtml),
                    urls.Select(u =>
                    {
                        var arPath = u.Path.Contains("lang=en", StringComparison.Ordinal)
                            ? u.Path.Replace("lang=en", "lang=ar", StringComparison.Ordinal)
                            : u.Path;
                        var enPath = u.Path.Contains("lang=ar", StringComparison.Ordinal)
                            ? u.Path.Replace("lang=ar", "lang=en", StringComparison.Ordinal)
                            : u.Path;

                        return new XElement(ns + "url",
                            new XElement(ns + "loc", _siteBase + u.Path),
                            new XElement(ns + "changefreq", u.Changefreq),
                            new XElement(ns + "priority", u.Priority),
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
                    })));

            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + doc.ToString(SaveOptions.DisableFormatting);
            return Content(xml, "application/xml", Encoding.UTF8);
        }
    }
}
