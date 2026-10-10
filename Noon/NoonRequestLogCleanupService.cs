using Affiliate.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Affiliate.Noon
{
    /// <summary>Periodically trims <see cref="Models.NoonRequestLog"/> to the newest rows.</summary>
    public sealed class NoonRequestLogCleanupService(
        IServiceProvider services,
        IOptions<NoonOptions> options,
        ILogger<NoonRequestLogCleanupService> logger) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(3);
        private const int DeleteBatchSize = 500;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Let the host finish startup (migrate/seed) before the first delete.
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

                using var timer = new PeriodicTimer(Interval);
                do
                {
                    await CleanupAsync(stoppingToken);
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }

        private async Task CleanupAsync(CancellationToken ct)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AffiliateDbContext>();
                var keep = Math.Max(1, options.Value.RequestLogKeepCount);

                var keepFromId = await db.NoonRequestLogs
                    .OrderByDescending(l => l.Id)
                    .Skip(keep - 1)
                    .Select(l => (long?)l.Id)
                    .FirstOrDefaultAsync(ct);
                if (keepFromId is null)
                    return;

                int deleted, total = 0;
                do
                {
                    var batch = await db.NoonRequestLogs
                        .Where(l => l.Id < keepFromId)
                        .OrderBy(l => l.Id)
                        .Take(DeleteBatchSize)
                        .Select(l => l.Id)
                        .ToListAsync(ct);
                    deleted = await db.NoonRequestLogs.Where(l => batch.Contains(l.Id)).ExecuteDeleteAsync(ct);
                    total += deleted;
                }
                while (deleted > 0);

                logger.LogInformation("Noon request log cleanup deleted {Deleted} rows (kept last {Keep})", total, keep);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in Noon request log cleanup");
            }
        }
    }
}
