using System.ComponentModel.DataAnnotations;
using Affiliate.Models;

namespace Affiliate.ViewModels
{
    public class PublishAuditLogFilterViewModel
    {
        [Display(Name = "ASIN")]
        public string? Asin { get; set; }

        [Display(Name = "Event")]
        public PublishAuditEvent? EventType { get; set; }

        /// <summary><c>direct</c>, <c>proxy</c>, or null for all.</summary>
        [Display(Name = "Route")]
        public string? Route { get; set; }

        /// <summary><c>Dp</c>, <c>Cart</c>, or null.</summary>
        [Display(Name = "Page")]
        public string? PageKind { get; set; }

        public bool? FailuresOnly { get; set; }

        public Guid? CorrelationId { get; set; }

        [DataType(DataType.Date)]
        public DateTime? From { get; set; }

        [DataType(DataType.Date)]
        public DateTime? To { get; set; }

        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }

    public class PublishAuditLogListItem
    {
        public long Id { get; set; }
        public Guid CorrelationId { get; set; }
        public string Asin { get; set; } = string.Empty;
        public PublishAuditEvent EventType { get; set; }
        public DateTime OccurredAt { get; set; }
        public string? Route { get; set; }
        public string? PageKind { get; set; }
        public bool? Success { get; set; }
        /// <summary>This event's own duration only (typically Attempt).</summary>
        public int? DurationMs { get; set; }
        /// <summary>Wall-clock for the whole publish session (first→last event of CorrelationId).</summary>
        public int? SessionDurationMs { get; set; }
        public string? Detail { get; set; }
        public string? ProductName { get; set; }
        public string? ImageUrl { get; set; }
        public int? ProductId { get; set; }

        /// <summary>Arabic one-liner for the Details column.</summary>
        public string ReadableDetail { get; set; } = "—";
    }

    public class PublishAuditSessionSummary
    {
        public string Asin { get; set; } = string.Empty;
        public string? ProductName { get; set; }
        public int WallClockSeconds { get; set; }
        public int AttemptCount { get; set; }
        public int FailedAttemptCount { get; set; }
        public string? WinningRoute { get; set; }
        public string? PageKind { get; set; }
        public bool CartFellBack { get; set; }
        public bool PrepareSucceeded { get; set; }
        public bool PreviewSent { get; set; }
        public bool Confirmed { get; set; }
        public bool Cancelled { get; set; }
        public IReadOnlyList<string> Lines { get; set; } = [];
    }

    public class PublishAuditLogsIndexViewModel
    {
        public PublishAuditLogFilterViewModel Filter { get; set; } = new();
        public IReadOnlyList<PublishAuditLogListItem> Logs { get; set; } = [];
        public PublishAuditSessionSummary? SessionSummary { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
        public int FailedOnPageCount => Logs.Count(l => l.Success == false);
        public int CartFallbackOnPageCount => Logs.Count(l => l.EventType == PublishAuditEvent.CartFallback);
    }
}
