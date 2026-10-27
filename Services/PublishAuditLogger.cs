using System.Collections.Concurrent;
using System.Threading.Channels;
using Affiliate.Data;
using Affiliate.Models;

namespace Affiliate.Services
{
    public interface IPublishAuditLogger
    {
        /// <summary>Enqueue one audit row. Never touches the database — safe on the Playwright hot path.</summary>
        void Log(
            Guid correlationId,
            string asin,
            PublishAuditEvent eventType,
            string? route = null,
            string? proxyHost = null,
            int? proxyPort = null,
            string? pageKind = null,
            bool? success = null,
            int? durationMs = null,
            string? detail = null,
            string? chatId = null,
            string? draftToken = null);
    }

    /// <summary>Singleton in-memory sink. A hosted flusher drains the queue in batches.</summary>
    public sealed class PublishAuditLogger : IPublishAuditLogger
    {
        private const int MaxDetailLength = 500;
        private readonly ConcurrentQueue<PublishAuditLog> _queue = new();
        private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

        public void Log(
            Guid correlationId,
            string asin,
            PublishAuditEvent eventType,
            string? route = null,
            string? proxyHost = null,
            int? proxyPort = null,
            string? pageKind = null,
            bool? success = null,
            int? durationMs = null,
            string? detail = null,
            string? chatId = null,
            string? draftToken = null)
        {
            if (asin is { Length: > 16 })
                asin = asin[..16];
            if (detail is { Length: > MaxDetailLength })
                detail = detail[..MaxDetailLength];

            _queue.Enqueue(new PublishAuditLog
            {
                CorrelationId = correlationId,
                Asin = asin ?? string.Empty,
                EventType = eventType,
                OccurredAt = DateTime.UtcNow,
                Route = route,
                ProxyHost = proxyHost,
                ProxyPort = proxyPort,
                PageKind = pageKind,
                Success = success,
                DurationMs = durationMs,
                Detail = detail,
                ChatId = chatId,
                DraftToken = draftToken
            });
            _signal.Writer.TryWrite(true);
        }

        internal ChannelReader<bool> Signal => _signal.Reader;

        internal int Drain(List<PublishAuditLog> dest, int max)
        {
            var n = 0;
            while (n < max && _queue.TryDequeue(out var entry))
            {
                dest.Add(entry);
                n++;
            }
            return n;
        }
    }

    /// <summary>Batch-inserts queued <see cref="PublishAuditLog"/> rows on signal or idle tick.</summary>
    public sealed class PublishAuditFlushService(
        PublishAuditLogger logger,
        IServiceProvider services,
        ILogger<PublishAuditFlushService> log) : BackgroundService
    {
        private static readonly TimeSpan IdleFlushInterval = TimeSpan.FromSeconds(2);
        private const int BatchSize = 100;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var buffer = new List<PublishAuditLog>(BatchSize);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(IdleFlushInterval);
                    try
                    {
                        await logger.Signal.ReadAsync(cts.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Idle tick — flush whatever is pending.
                    }

                    while (logger.Drain(buffer, BatchSize) > 0)
                    {
                        await FlushBatchAsync(buffer, stoppingToken);
                        buffer.Clear();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Publish audit flush failed; will retry");
                    buffer.Clear();
                    try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                }
            }

            buffer.Clear();
            while (logger.Drain(buffer, BatchSize) > 0)
            {
                try { await FlushBatchAsync(buffer, CancellationToken.None); }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Publish audit final flush failed; {Count} rows dropped", buffer.Count);
                }
                buffer.Clear();
            }
        }

        private async Task FlushBatchAsync(List<PublishAuditLog> batch, CancellationToken ct)
        {
            if (batch.Count == 0)
                return;

            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AffiliateDbContext>();
            db.PublishAuditLogs.AddRange(batch);
            await db.SaveChangesAsync(ct);
        }
    }
}
