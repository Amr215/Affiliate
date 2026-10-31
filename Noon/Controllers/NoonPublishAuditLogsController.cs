using Affiliate.Data;
using Affiliate.Noon.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Noon.Controllers
{
    public class NoonPublishAuditLogsController(AffiliateDbContext context) : Controller
    {
        public async Task<IActionResult> Index(
            [FromQuery] NoonPublishAuditLogFilterViewModel filter,
            CancellationToken cancellationToken)
        {
            NormalizeFilter(filter);

            var query = context.NoonPublishAuditLogs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(filter.Sku))
            {
                var sku = filter.Sku.Trim().ToUpperInvariant();
                query = query.Where(l => l.Sku == sku);
            }

            if (filter.EventType.HasValue)
                query = query.Where(l => l.EventType == filter.EventType.Value);

            if (filter.CorrelationId.HasValue)
                query = query.Where(l => l.CorrelationId == filter.CorrelationId.Value);

            if (filter.FailuresOnly == true)
                query = query.Where(l => l.Success == false);

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
                .Select(l => new NoonPublishAuditLogListItem
                {
                    Id = l.Id,
                    CorrelationId = l.CorrelationId,
                    Sku = l.Sku,
                    EventType = l.EventType,
                    OccurredAt = l.OccurredAt,
                    Route = l.Route,
                    PageKind = l.PageKind,
                    Success = l.Success,
                    DurationMs = l.DurationMs,
                    Detail = l.Detail
                })
                .ToListAsync(cancellationToken);

            var skus = rows.Select(r => r.Sku).Where(s => s.Length > 0).Distinct().ToList();
            if (skus.Count > 0)
            {
                var products = await context.NoonProducts.AsNoTracking()
                    .Where(p => skus.Contains(p.Sku))
                    .Select(p => new { p.Id, p.Sku, p.Name, p.ImageUrl })
                    .ToListAsync(cancellationToken);

                var bySku = products
                    .GroupBy(p => p.Sku, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var row in rows)
                {
                    if (!bySku.TryGetValue(row.Sku, out var product))
                        continue;
                    row.ProductId = product.Id;
                    row.ProductName = product.Name;
                    row.ImageUrl = product.ImageUrl;
                }
            }

            await FillSessionDurationsAsync(rows, cancellationToken);

            foreach (var row in rows)
                row.ReadableDetail = BuildReadableDetail(row);

            NoonPublishAuditSessionSummary? session = null;
            if (filter.CorrelationId.HasValue && rows.Count > 0)
                session = BuildSessionSummary(rows);

            return View(new NoonPublishAuditLogsIndexViewModel
            {
                Filter = filter,
                Logs = rows,
                SessionSummary = session,
                TotalCount = totalCount,
                TotalPages = totalPages
            });
        }

        private static void NormalizeFilter(NoonPublishAuditLogFilterViewModel filter)
        {
            filter.Page = Math.Max(1, filter.Page);
            filter.PageSize = filter.PageSize switch
            {
                25 or 50 or 100 or 200 => filter.PageSize,
                _ => 50
            };

            if (!string.IsNullOrWhiteSpace(filter.Sku))
                filter.Sku = filter.Sku.Trim().ToUpperInvariant();
        }

        private async Task FillSessionDurationsAsync(
            List<NoonPublishAuditLogListItem> rows,
            CancellationToken cancellationToken)
        {
            var ids = rows.Select(r => r.CorrelationId).Distinct().ToList();
            if (ids.Count == 0)
                return;

            var bounds = await context.NoonPublishAuditLogs.AsNoTracking()
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
            string.IsNullOrWhiteSpace(route) ? "غير معروف" : $"بروكسي {route}";

        private static string BuildReadableDetail(NoonPublishAuditLogListItem log)
        {
            var raw = string.IsNullOrWhiteSpace(log.Detail) ? null : log.Detail.Trim();
            var own = FormatDuration(log.DurationMs);
            var session = FormatDuration(log.SessionDurationMs);

            return log.EventType switch
            {
                NoonPublishAuditEvent.Requested =>
                    raw is null ? "ضغط زر النشر وبدأ التجهيز."
                    : raw.StartsWith("template=", StringComparison.OrdinalIgnoreCase)
                        ? $"ضغط زر النشر (قالب: {raw["template=".Length..]})."
                        : $"ضغط زر النشر — {raw}.",

                NoonPublishAuditEvent.Attempt when log.Success == true =>
                    $"محاولة ناجحة عبر {RouteLabel(log.Route)} — صفحة المنتج" +
                    (raw is null ? "" : $" — {raw}") +
                    $" — مدة هذه المحاولة: {own}.",

                NoonPublishAuditEvent.Attempt =>
                    $"محاولة فاشلة عبر {RouteLabel(log.Route)} — مدة هذه المحاولة: {own}" +
                    (raw is null ? "." : $" — {raw}"),

                NoonPublishAuditEvent.Finished when log.Success == true =>
                    $"انتهى التجهيز بنجاح عبر {RouteLabel(log.Route)} — صفحة المنتج." +
                    (session == "—" ? "" : $" مدة النشر الكاملة للجلسة: {session}."),

                NoonPublishAuditEvent.Finished =>
                    "انتهى التجهيز بالفشل." + (raw is null ? "" : $" — {raw}"),

                NoonPublishAuditEvent.Failed =>
                    "فشل التجهيز كلياً." + (raw is null ? "" : $" — {raw}"),

                NoonPublishAuditEvent.PreviewSent =>
                    "أُرسلت معاينة النشر إلى تيليجرام.",

                NoonPublishAuditEvent.Confirmed =>
                    raw is null ? "تم تأكيد النشر."
                    : raw.Contains("text", StringComparison.OrdinalIgnoreCase) ? "تم تأكيد النشر (نص فقط)."
                    : "تم تأكيد النشر (مع صورة).",

                NoonPublishAuditEvent.Cancelled =>
                    "أُلغي النشر من المعاينة.",

                _ => raw ?? "—"
            };
        }

        private static NoonPublishAuditSessionSummary BuildSessionSummary(
            IReadOnlyList<NoonPublishAuditLogListItem> rows)
        {
            var chrono = rows.OrderBy(r => r.OccurredAt).ThenBy(r => r.Id).ToList();
            var first = chrono[0];
            var last = chrono[^1];
            var wallSec = Math.Max(0, (int)Math.Round((last.OccurredAt - first.OccurredAt).TotalSeconds));

            var attempts = chrono.Where(r => r.EventType == NoonPublishAuditEvent.Attempt).ToList();
            var failedAttempts = attempts.Count(a => a.Success == false);
            var winner = attempts.LastOrDefault(a => a.Success == true)
                         ?? chrono.LastOrDefault(r =>
                             r.EventType is NoonPublishAuditEvent.Finished or NoonPublishAuditEvent.Failed);

            var finished = chrono.LastOrDefault(r =>
                r.EventType is NoonPublishAuditEvent.Finished or NoonPublishAuditEvent.Failed);
            var prepareOk = finished?.EventType == NoonPublishAuditEvent.Finished && finished.Success == true;
            var preview = chrono.Any(r => r.EventType == NoonPublishAuditEvent.PreviewSent);
            var confirmed = chrono.Any(r => r.EventType == NoonPublishAuditEvent.Confirmed);
            var cancelled = chrono.Any(r => r.EventType == NoonPublishAuditEvent.Cancelled);
            var route = winner?.Route ?? finished?.Route;

            var sessionMs = chrono[0].SessionDurationMs
                            ?? Math.Max(0, (int)(last.OccurredAt - first.OccurredAt).TotalMilliseconds);

            var lines = new List<string>
            {
                $"مدة النشر الكاملة: {FormatDuration(sessionMs)} (من أول حدث لآخر حدث).",
                $"محاولات اللقطة: {attempts.Count} (فاشلة: {failedAttempts}) — كل مدة تحت «مدة الحدث» خاصة بمحاولتها فقط."
            };

            foreach (var a in attempts)
            {
                var mark = a.Success == true ? "✓" : "✗";
                lines.Add(
                    $"{mark} {RouteLabel(a.Route)} — مدة المحاولة {FormatDuration(a.DurationMs)}" +
                    (string.IsNullOrWhiteSpace(a.Detail) ? "" : $" — {a.Detail}"));
            }

            if (prepareOk)
                lines.Add($"التجهيز نجح عبر {RouteLabel(route)} — صفحة المنتج.");
            else if (finished is not null)
                lines.Add("التجهيز فشل.");

            if (preview) lines.Add("المعاينة وصلت تيليجرام.");
            if (confirmed) lines.Add("تم التأكيد والنشر.");
            if (cancelled) lines.Add("تم الإلغاء.");

            return new NoonPublishAuditSessionSummary
            {
                Sku = first.Sku,
                ProductName = first.ProductName,
                WallClockSeconds = wallSec,
                AttemptCount = attempts.Count,
                FailedAttemptCount = failedAttempts,
                WinningRoute = route,
                PrepareSucceeded = prepareOk,
                PreviewSent = preview,
                Confirmed = confirmed,
                Cancelled = cancelled,
                Lines = lines
            };
        }
    }
}
