using Affiliate.Data;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Services
{
    /// <summary>
    /// Periodically trims <see cref="Models.OxylabsRequestLog"/> so only the newest rows remain.
    /// </summary>
    public sealed class OxylabsRequestLogCleanupService(
        IServiceProvider services,
        ILogger<OxylabsRequestLogCleanupService> logger) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(3);
        private const int KeepCount = 100;
        private const int DeleteBatchSize = 500;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "Oxylabs request log cleanup started (every {Interval}; keep last {KeepCount} rows)",
                Interval, KeepCount);

            // Let the host finish startup (migrate/seed) before the first heavy delete.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            using var timer = new PeriodicTimer(Interval);

            do
            {
                await CleanupAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task CleanupAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AffiliateDbContext>();
                db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));

                var total = await db.OxylabsRequestLogs.CountAsync(cancellationToken);
                logger.LogInformation(
                    "Oxylabs request log cleanup running — {Total} rows present, keeping {KeepCount}",
                    total, KeepCount);

                if (total <= KeepCount)
                {
                    logger.LogInformation("Oxylabs request log cleanup — nothing to delete");
                    return;
                }

                // Newest KeepCount rows by Id (identity). The KeepCount-th newest is the floor to keep.
                var keepFromId = await db.OxylabsRequestLogs
                    .OrderByDescending(l => l.Id)
                    .Skip(KeepCount - 1)
                    .Select(l => l.Id)
                    .FirstAsync(cancellationToken);

                var deletedTotal = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Prefer a stable Id list over OFFSET on a huge table.
                    var batchIds = await db.OxylabsRequestLogs
                        .Where(l => l.Id < keepFromId)
                        .OrderBy(l => l.Id)
                        .Take(DeleteBatchSize)
                        .Select(l => l.Id)
                        .ToListAsync(cancellationToken);

                    if (batchIds.Count == 0)
                        break;

                    var deleted = await db.OxylabsRequestLogs
                        .Where(l => batchIds.Contains(l.Id))
                        .ExecuteDeleteAsync(cancellationToken);

                    deletedTotal += deleted;
                    logger.LogInformation(
                        "Oxylabs request log cleanup deleted batch of {Deleted} (running total {Total})",
                        deleted, deletedTotal);

                    if (deleted == 0)
                        break;
                }

                logger.LogInformation(
                    "Oxylabs request log cleanup finished — deleted {Deleted} rows (kept last {KeepCount})",
                    deletedTotal, KeepCount);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in Oxylabs request log cleanup");
            }
        }
    }
}
