using System.ComponentModel.DataAnnotations;

namespace Affiliate.ViewModels
{
    public class OxylabsLogFilterViewModel
    {
        public int? ScraperSearchId { get; set; }

        [Display(Name = "Status")]
        public int? StatusCode { get; set; }

        /// <summary>When true, only rows with StatusCode != 200.</summary>
        public bool? ErrorsOnly { get; set; }

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
        public IReadOnlyList<ScraperSearchOption> Searches { get; set; } = [];
    }

    public class OxylabsRequestLogListItem
    {
        public long Id { get; set; }
        public int? ScraperSearchId { get; set; }
        public string SearchName { get; set; } = string.Empty;
        public int Page { get; set; }
        public DateTime RequestedAt { get; set; }
        public int StatusCode { get; set; }
        public string? StatusPhrase { get; set; }
        public bool HasResponseBody { get; set; }
    }

    public class ScraperSearchOption
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Query { get; set; } = string.Empty;

        public string DisplayLabel =>
            string.IsNullOrWhiteSpace(Query) ? Name : $"{Name} — {Query}";
    }
}
