using System.ComponentModel.DataAnnotations;

namespace Affiliate.Noon.Models
{
    public class NoonPublishAuditLogFilterViewModel
    {
        [Display(Name = "SKU")]
        public string? Sku { get; set; }

        [Display(Name = "Event")]
        public NoonPublishAuditEvent? EventType { get; set; }

        public bool? FailuresOnly { get; set; }

        public Guid? CorrelationId { get; set; }

        [DataType(DataType.Date)]
        public DateTime? From { get; set; }

        [DataType(DataType.Date)]
        public DateTime? To { get; set; }

        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }

    public class NoonPublishAuditLogListItem
    {
        public long Id { get; set; }
        public Guid CorrelationId { get; set; }
        public string Sku { get; set; } = string.Empty;
        public NoonPublishAuditEvent EventType { get; set; }
        public DateTime OccurredAt { get; set; }
        public string? Route { get; set; }
        public string? PageKind { get; set; }
        public bool? Success { get; set; }
        public int? DurationMs { get; set; }
        public int? SessionDurationMs { get; set; }
        public string? Detail { get; set; }
        public string? ProductName { get; set; }
        public string? ImageUrl { get; set; }
        public int? ProductId { get; set; }
        public string ReadableDetail { get; set; } = "—";
    }

    public class NoonPublishAuditSessionSummary
    {
        public string Sku { get; set; } = string.Empty;
        public string? ProductName { get; set; }
        public int WallClockSeconds { get; set; }
        public int AttemptCount { get; set; }
        public int FailedAttemptCount { get; set; }
        public string? WinningRoute { get; set; }
        public bool PrepareSucceeded { get; set; }
        public bool PreviewSent { get; set; }
        public bool Confirmed { get; set; }
        public bool Cancelled { get; set; }
        public IReadOnlyList<string> Lines { get; set; } = [];
    }

    public class NoonPublishAuditLogsIndexViewModel
    {
        public NoonPublishAuditLogFilterViewModel Filter { get; set; } = new();
        public IReadOnlyList<NoonPublishAuditLogListItem> Logs { get; set; } = [];
        public NoonPublishAuditSessionSummary? SessionSummary { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
        public int FailedOnPageCount => Logs.Count(l => l.Success == false);
    }
}
