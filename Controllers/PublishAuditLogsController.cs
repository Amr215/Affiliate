using Affiliate.Data;
using Affiliate.Models;
using Affiliate.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Controllers
{
    public class PublishAuditLogsController : Controller
    {
        private readonly AffiliateDbContext _context;

        public PublishAuditLogsController(AffiliateDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index(
            [FromQuery] PublishAuditLogFilterViewModel filter,
            CancellationToken cancellationToken)
        {
            NormalizeFilter(filter);

            var query = _context.PublishAuditLogs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(filter.Asin))
            {
                var asin = filter.Asin.Trim().ToUpperInvariant();
                query = query.Where(l => l.Asin == asin);
            }

            if (filter.EventType.HasValue)
                query = query.Where(l => l.EventType == filter.EventType.Value);

            if (filter.CorrelationId.HasValue)
                query = query.Where(l => l.CorrelationId == filter.CorrelationId.Value);

            if (!string.IsNullOrWhiteSpace(filter.PageKind))
                query = query.Where(l => l.PageKind == filter.PageKind);

            if (filter.FailuresOnly == true)
                query = query.Where(l => l.Success == false);

            if (string.Equals(filter.Route, "direct", StringComparison.OrdinalIgnoreCase))
                query = query.Where(l => l.Route == "direct");
            else if (string.Equals(filter.Route, "proxy", StringComparison.OrdinalIgnoreCase))
                query = query.Where(l => l.Route != null && l.Route != "direct");

            if (filter.From.HasValue)
                query = query.Where(l => l.OccurredAt >= filter.From.Value.Date);

            if (filter.To.HasValue)
                query = query.Where(l => l.OccurredAt < filter.To.Value.Date.AddDays(1));

            var totalCount = await query.CountAsync(cancellationToken);
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)filter.PageSize));
            if (filter.Page > totalPages)
                filter.Page = totalPages;

            var rows = await query
                .OrderByDescending(l => l.OccurredAt)
                .ThenByDescending(l => l.Id)
                .Skip((filter.Page - 1) * filter.PageSize)
                .Take(filter.PageSize)
                .Select(l => new PublishAuditLogListItem
                {
                    Id = l.Id,
                    CorrelationId = l.CorrelationId,
                    Asin = l.Asin,
                    EventType = l.EventType,
                    OccurredAt = l.OccurredAt,
                    Route = l.Route,
                    PageKind = l.PageKind,
                    Success = l.Success,
                    DurationMs = l.DurationMs,
                    Detail = l.Detail
                })
                .ToListAsync(cancellationToken);

            var asins = rows.Select(r => r.Asin).Where(a => a.Length > 0).Distinct().ToList();
            if (asins.Count > 0)
            {
                var products = await _context.Products.AsNoTracking()
                    .Where(p => p.Asin != null && asins.Contains(p.Asin))
                    .Select(p => new { p.Id, p.Asin, p.Name, p.ImageUrl })
                    .ToListAsync(cancellationToken);

                var byAsin = products
                    .Where(p => !string.IsNullOrEmpty(p.Asin))
                    .GroupBy(p => p.Asin!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var row in rows)
                {
                    if (!byAsin.TryGetValue(row.Asin, out var product))
                        continue;
                    row.ProductId = product.Id;
                    row.ProductName = product.Name;
                    row.ImageUrl = product.ImageUrl;
                }
            }

            await FillSessionDurationsAsync(rows, cancellationToken);

            foreach (var row in rows)
                row.ReadableDetail = BuildReadableDetail(row);

            PublishAuditSessionSummary? session = null;
            if (filter.CorrelationId.HasValue && rows.Count > 0)
                session = BuildSessionSummary(rows);

            return View(new PublishAuditLogsIndexViewModel
            {
                Filter = filter,
                Logs = rows,
                SessionSummary = session,
                TotalCount = totalCount,
                TotalPages = totalPages
            });
        }

        private static void NormalizeFilter(PublishAuditLogFilterViewModel filter)
        {
            filter.Page = Math.Max(1, filter.Page);
            filter.PageSize = filter.PageSize switch
            {
                25 or 50 or 100 or 200 => filter.PageSize,
                _ => 50
            };

            if (!string.IsNullOrWhiteSpace(filter.Asin))
                filter.Asin = filter.Asin.Trim().ToUpperInvariant();

            if (filter.PageKind is not ("Dp" or "Cart"))
                filter.PageKind = null;

            if (filter.Route is not ("direct" or "proxy"))
                filter.Route = null;
        }

        private async Task FillSessionDurationsAsync(
            List<PublishAuditLogListItem> rows,
            CancellationToken cancellationToken)
        {
            var ids = rows.Select(r => r.CorrelationId).Distinct().ToList();
            if (ids.Count == 0)
                return;

            var bounds = await _context.PublishAuditLogs.AsNoTracking()
                .Where(l => ids.Contains(l.CorrelationId))
                .GroupBy(l => l.CorrelationId)
                .Select(g => new
                {
                    CorrelationId = g.Key,
                    First = g.Min(x => x.OccurredAt),
                    Last = g.Max(x => x.OccurredAt)
                })
                .ToListAsync(cancellationToken);

            var byId = bounds.ToDictionary(b => b.CorrelationId);
            foreach (var row in rows)
            {
                if (!byId.TryGetValue(row.CorrelationId, out var b))
                    continue;
                row.SessionDurationMs = Math.Max(0, (int)(b.Last - b.First).TotalMilliseconds);
            }
        }

        internal static string FormatDuration(int? ms)
        {
            if (ms is null or < 0)
                return "—";
            if (ms < 1000)
                return $"{ms} مللي ثانية";
            return $"{ms.Value / 1000.0:0.#} ثانية";
        }

        private static string RouteLabel(string? route) =>
            string.IsNullOrWhiteSpace(route) ? "غير معروف"
            : route.Equals("direct", StringComparison.OrdinalIgnoreCase) ? "مباشر (بدون بروكسي)"
            : $"بروكسي {route}";

        private static string PageLabel(string? pageKind) => pageKind switch
        {
            "Cart" => "صفحة السلة",
            "Dp" => "صفحة المنتج",
            _ => "—"
        };

        private static string BuildReadableDetail(PublishAuditLogListItem log)
        {
            var raw = string.IsNullOrWhiteSpace(log.Detail) ? null : log.Detail.Trim();
            var own = FormatDuration(log.DurationMs);
            var session = FormatDuration(log.SessionDurationMs);

            return log.EventType switch
            {
                PublishAuditEvent.Requested =>
                    raw is null ? "ضغط زر النشر وبدأ التجهيز."
                    : raw.StartsWith("template=", StringComparison.OrdinalIgnoreCase)
                        ? $"ضغط زر النشر (قالب: {raw["template=".Length..]})."
                        : $"ضغط زر النشر — {raw}.",

                PublishAuditEvent.Attempt when log.Success == true =>
                    $"محاولة ناجحة عبر {RouteLabel(log.Route)} — {PageLabel(log.PageKind)}" +
                    (raw is null ? "" : $" — {raw}") +
                    $" — مدة هذه المحاولة: {own}.",

                PublishAuditEvent.Attempt =>
                    $"محاولة فاشلة عبر {RouteLabel(log.Route)} — مدة هذه المحاولة: {own}" +
                    (raw is null ? "." : $" — {raw}"),

                PublishAuditEvent.CartFallback =>
                    raw is null
                        ? "مسار السلة فشل فتم الرجوع لصفحة المنتج."
                        : raw,

                PublishAuditEvent.Finished when log.Success == true =>
                    $"انتهى التجهيز بنجاح عبر {RouteLabel(log.Route)} — {PageLabel(log.PageKind)}." +
                    (session == "—" ? "" : $" مدة النشر الكاملة للجلسة: {session}."),

                PublishAuditEvent.Finished =>
                    "انتهى التجهيز بالفشل." + (raw is null ? "" : $" — {raw}"),

                PublishAuditEvent.Failed =>
                    "فشل التجهيز كلياً." + (raw is null ? "" : $" — {raw}"),

                PublishAuditEvent.PreviewSent =>
                    "أُرسلت معاينة النشر إلى تيليجرام.",

                PublishAuditEvent.Confirmed =>
                    raw is null ? "تم تأكيد النشر."
                    : raw.Contains("text", StringComparison.OrdinalIgnoreCase) ? "تم تأكيد النشر (نص فقط)."
                    : "تم تأكيد النشر (مع صورة).",

                PublishAuditEvent.Cancelled =>
                    "أُلغي النشر من المعاينة.",

                _ => raw ?? "—"
            };
        }

        private static PublishAuditSessionSummary BuildSessionSummary(IReadOnlyList<PublishAuditLogListItem> rows)
        {
            // Rows are newest-first; chronological for summary.
            var chrono = rows.OrderBy(r => r.OccurredAt).ThenBy(r => r.Id).ToList();
            var first = chrono[0];
            var last = chrono[^1];
            var wallSec = Math.Max(0, (int)Math.Round((last.OccurredAt - first.OccurredAt).TotalSeconds));

            var attempts = chrono.Where(r => r.EventType == PublishAuditEvent.Attempt).ToList();
            var failedAttempts = attempts.Count(a => a.Success == false);
            var winner = attempts.LastOrDefault(a => a.Success == true)
                         ?? chrono.LastOrDefault(r => r.EventType is PublishAuditEvent.Finished or PublishAuditEvent.Failed);

            var cartFallback = chrono.Any(r => r.EventType == PublishAuditEvent.CartFallback);
            var finished = chrono.LastOrDefault(r => r.EventType is PublishAuditEvent.Finished or PublishAuditEvent.Failed);
            var prepareOk = finished?.EventType == PublishAuditEvent.Finished && finished.Success == true;
            var preview = chrono.Any(r => r.EventType == PublishAuditEvent.PreviewSent);
            var confirmed = chrono.Any(r => r.EventType == PublishAuditEvent.Confirmed);
            var cancelled = chrono.Any(r => r.EventType == PublishAuditEvent.Cancelled);

            var pageKind = winner?.PageKind
                           ?? chrono.LastOrDefault(r => r.PageKind != null)?.PageKind;
            var route = winner?.Route ?? finished?.Route;

            var sessionMs = chrono[0].SessionDurationMs
                            ?? Math.Max(0, (int)(last.OccurredAt - first.OccurredAt).TotalMilliseconds);

            var lines = new List<string>
            {
                $"مدة النشر الكاملة: {FormatDuration(sessionMs)} (من أول حدث لآخر حدث).",
                $"محاولات اللقطة: {attempts.Count} (فاشلة: {failedAttempts}) — كل مدة تحت «مدة الحدث» خاصة بمحاولتها فقط."
            };

            if (attempts.Count > 0)
            {
                foreach (var a in attempts)
                {
                    var mark = a.Success == true ? "✓" : "✗";
                    lines.Add(
                        $"{mark} {RouteLabel(a.Route)} — مدة المحاولة {FormatDuration(a.DurationMs)}" +
                        (string.IsNullOrWhiteSpace(a.Detail) ? "" : $" — {a.Detail}"));
                }
            }

            if (cartFallback)
            {
                var fb = chrono.LastOrDefault(r => r.EventType == PublishAuditEvent.CartFallback);
                lines.Add(string.IsNullOrWhiteSpace(fb?.Detail)
                    ? "مسار السلة فشل مرة ثم رجع لصفحة المنتج."
                    : fb!.Detail!);
            }

            if (prepareOk)
                lines.Add($"التجهيز نجح عبر {RouteLabel(route)} — {PageLabel(pageKind)}.");
            else if (finished is not null)
                lines.Add("التجهيز فشل.");

            if (preview) lines.Add("المعاينة وصلت تيليجرام.");
            if (confirmed) lines.Add("تم التأكيد والنشر.");
            if (cancelled) lines.Add("تم الإلغاء.");

            return new PublishAuditSessionSummary
            {
                Asin = first.Asin,
                ProductName = first.ProductName,
                WallClockSeconds = wallSec,
                AttemptCount = attempts.Count,
                FailedAttemptCount = failedAttempts,
                WinningRoute = route,
                PageKind = pageKind,
                CartFellBack = cartFallback,
                PrepareSucceeded = prepareOk,
                PreviewSent = preview,
                Confirmed = confirmed,
                Cancelled = cancelled,
                Lines = lines
            };
        }
    }
}
