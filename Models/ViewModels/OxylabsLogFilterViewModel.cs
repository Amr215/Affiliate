using System.ComponentModel.DataAnnotations;

namespace Affiliate.ViewModels
{
    public class OxylabsLogFilterViewModel
    {
        public int? ScraperUrlId { get; set; }

        [Display(Name = "Status")]
        public int? StatusCode { get; set; }

        [Display(Name = "Port")]
        public int? Port { get; set; }

        /// <summary>When true, only rows with StatusCode != 200.</summary>
        public bool? ErrorsOnly { get; set; }

        /// <summary>True = Google Translate route only, false = direct only, null = both.</summary>
        [Display(Name = "Route")]
        public bool? ViaTranslate { get; set; }

        [DataType(DataType.Date)]
        public DateTime? From { get; set; }

        [DataType(DataType.Date)]
        public DateTime? To { get; set; }

        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 25;
    }

    public class OxylabsLogsIndexViewModel
    {
        public OxylabsLogFilterViewModel Filter { get; set; } = new();
        public IReadOnlyList<OxylabsRequestLogListItem> Logs { get; set; } = [];
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
        public IReadOnlyList<ScraperUrlOption> Searches { get; set; } = [];

        /// <summary>Successful (HTTP 200) requests fetched straight from Amazon, across the whole filter.</summary>
        public int SuccessDirectCount { get; set; }

        /// <summary>Successful (HTTP 200) requests fetched through Google Translate, across the whole filter.</summary>
        public int SuccessTranslateCount { get; set; }

        /// <summary>Successful requests among the rows currently displayed.</summary>
        public int SuccessOnPageCount => Logs.Count(l => l.StatusCode == 200);
    }

    public class OxylabsRequestLogListItem
    {
        public long Id { get; set; }
        public int? ScraperUrlId { get; set; }
        public string SearchName { get; set; } = string.Empty;
        public int Page { get; set; }
        public DateTime RequestedAt { get; set; }
        public int StatusCode { get; set; }
        public string? StatusPhrase { get; set; }
        public int? Port { get; set; }
        public bool HasResponseBody { get; set; }

        /// <summary>
        /// Derived from the logged request URL — no stored column. True when the page was fetched
        /// through Google Translate because the port was blocked.
        /// </summary>
        public bool ViaGoogleTranslate { get; set; }
    }

    public class ScraperUrlOption
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;

        public string DisplayLabel =>
            string.IsNullOrWhiteSpace(Url) ? Name : $"{Name} — {Truncate(Url, 60)}";

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..max] + "…";
    }
}
