using System.Collections.Concurrent;
using System.Threading.Channels;
using Affiliate.Data;
using Affiliate.Noon.Models;

namespace Affiliate.Noon
{
    public interface INoonPublishAuditLogger
    {
        void Log(
            Guid correlationId,
            string sku,
            NoonPublishAuditEvent eventType,
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

    /// <summary>Singleton in-memory sink for Noon publish audit rows.</summary>
    public sealed class NoonPublishAuditLogger : INoonPublishAuditLogger
    {
        private const int MaxDetailLength = 500;
        private readonly ConcurrentQueue<NoonPublishAuditLog> _queue = new();
        private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

        public void Log(
            Guid correlationId,
            string sku,
            NoonPublishAuditEvent eventType,
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
            if (sku is { Length: > 64 })
                sku = sku[..64];
            if (detail is { Length: > MaxDetailLength })
                detail = detail[..MaxDetailLength];

            _queue.Enqueue(new NoonPublishAuditLog
            {
                CorrelationId = correlationId,
                Sku = sku ?? string.Empty,
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

        internal int Drain(List<NoonPublishAuditLog> dest, int max)
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

    public sealed class NoonPublishAuditFlushService(
        NoonPublishAuditLogger logger,
        IServiceProvider services,
        ILogger<NoonPublishAuditFlushService> log) : BackgroundService
    {
        private static readonly TimeSpan IdleFlushInterval = TimeSpan.FromSeconds(2);
        private const int BatchSize = 100;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var buffer = new List<NoonPublishAuditLog>(BatchSize);

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
                    log.LogError(ex, "Noon publish audit flush failed; will retry");
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
                    log.LogWarning(ex, "Noon publish audit final flush failed; {Count} rows dropped", buffer.Count);
                }
                buffer.Clear();
            }
        }

        private async Task FlushBatchAsync(List<NoonPublishAuditLog> batch, CancellationToken ct)
        {
            if (batch.Count == 0)
                return;

            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AffiliateDbContext>();
            db.NoonPublishAuditLogs.AddRange(batch);
            await db.SaveChangesAsync(ct);
        }
    }
}
