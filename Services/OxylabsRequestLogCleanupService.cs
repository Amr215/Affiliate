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

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "Oxylabs request log cleanup started (every {Interval}; keep last {KeepCount} rows)",
                Interval, KeepCount);

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

                // Newest KeepCount rows by Id (identity). The KeepCount-th newest is the floor to keep.
                var keepFromId = await db.OxylabsRequestLogs
                    .OrderByDescending(l => l.Id)
                    .Skip(KeepCount - 1)
                    .Select(l => (long?)l.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                if (keepFromId is null)
                {
                    logger.LogDebug(
                        "Oxylabs request log cleanup skipped — fewer than {KeepCount} rows",
                        KeepCount);
                    return;
                }

                var deleted = await db.OxylabsRequestLogs
                    .Where(l => l.Id < keepFromId.Value)
                    .ExecuteDeleteAsync(cancellationToken);

                if (deleted > 0)
                {
                    logger.LogInformation(
                        "Oxylabs request log cleanup deleted {Deleted} rows (kept last {KeepCount})",
                        deleted, KeepCount);
                }
                else
                {
                    logger.LogDebug("Oxylabs request log cleanup — nothing to delete");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in Oxylabs request log cleanup");
            }
        }
    }
}
