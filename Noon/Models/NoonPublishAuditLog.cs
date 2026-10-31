using System.ComponentModel.DataAnnotations;

namespace Affiliate.Noon.Models
{
    /// <summary>Milestones in the Noon Telegram publish pipeline.</summary>
    public enum NoonPublishAuditEvent
    {
        Requested = 0,
        Attempt = 1,
        Finished = 2,
        PreviewSent = 3,
        Confirmed = 4,
        Cancelled = 5,
        Failed = 6
    }

    /// <summary>
    /// One durable row per Noon publish milestone. Enqueued in-memory and flushed in the background
    /// so Playwright/Telegram paths never wait on SQL.
    /// </summary>
    public class NoonPublishAuditLog
    {
        public long Id { get; set; }

        /// <summary>Ties all events of one publish together.</summary>
        public Guid CorrelationId { get; set; }

        [StringLength(64)]
        public string Sku { get; set; } = string.Empty;

        public NoonPublishAuditEvent EventType { get; set; }

        public DateTime OccurredAt { get; set; }

        /// <summary><c>host:port</c> (never credentials). Noon publish is proxies-only.</summary>
        [StringLength(64)]
        public string? Route { get; set; }

        [StringLength(128)]
        public string? ProxyHost { get; set; }

        public int? ProxyPort { get; set; }

        /// <summary>Always <c>Dp</c> for Noon (product page only).</summary>
        [StringLength(8)]
        public string? PageKind { get; set; }

        public bool? Success { get; set; }
        public int? DurationMs { get; set; }

        [StringLength(500)]
        public string? Detail { get; set; }

        [StringLength(64)]
        public string? ChatId { get; set; }

        [StringLength(16)]
        public string? DraftToken { get; set; }
    }
}
