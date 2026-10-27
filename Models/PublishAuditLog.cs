using System.ComponentModel.DataAnnotations;

namespace Affiliate.Models
{
    /// <summary>Milestones in the Telegram publish pipeline.</summary>
    public enum PublishAuditEvent
    {
        Requested = 0,
        Attempt = 1,
        CartFallback = 2,
        Finished = 3,
        PreviewSent = 4,
        Confirmed = 5,
        Cancelled = 6,
        Failed = 7
    }

    /// <summary>
    /// One durable row per publish milestone. Enqueued in-memory and flushed in the background
    /// so Playwright/Telegram paths never wait on SQL.
    /// </summary>
    public class PublishAuditLog
    {
        public long Id { get; set; }

        /// <summary>Ties all events of one publish together.</summary>
        public Guid CorrelationId { get; set; }

        [StringLength(16)]
        public string Asin { get; set; } = string.Empty;

        public PublishAuditEvent EventType { get; set; }

        public DateTime OccurredAt { get; set; }

        /// <summary><c>direct</c> or <c>host:port</c> (never credentials).</summary>
        [StringLength(64)]
        public string? Route { get; set; }

        [StringLength(128)]
        public string? ProxyHost { get; set; }

        public int? ProxyPort { get; set; }

        /// <summary><c>Dp</c> or <c>Cart</c> when relevant.</summary>
        [StringLength(8)]
        public string? PageKind { get; set; }

        public bool? Success { get; set; }
        public int? DurationMs { get; set; }

        /// <summary>Short free-text context (truncated on enqueue).</summary>
        [StringLength(500)]
        public string? Detail { get; set; }

        [StringLength(64)]
        public string? ChatId { get; set; }

        [StringLength(16)]
        public string? DraftToken { get; set; }
    }
}
