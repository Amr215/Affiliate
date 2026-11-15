namespace Affiliate.Services
{
    public sealed class AmazonScraperBackgroundService(
        IServiceProvider services,
        ILogger<AmazonScraperBackgroundService> logger) : BackgroundService
    {
        /// <summary>How often we evaluate each <see cref="Affiliate.Models.ScraperUrl.IsDue"/> (independent of per-URL <c>IntervalSeconds</c>).</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "Amazon scraper scheduler started (poll every {Interval}; intervals from ScraperUrls).",
                PollInterval);

            ScheduleTick(stoppingToken);

            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                ScheduleTick(stoppingToken);
        }

        /// <summary>Does not block the timer loop — a slow multi-page scrape must not delay other due URLs.</summary>
        private void ScheduleTick(CancellationToken cancellationToken)
        {
            _ = RunTickAsync(cancellationToken).ContinueWith(
                t =>
                {
                    if (t.IsFaulted && t.Exception is not null)
                        logger.LogError(t.Exception.GetBaseException(), "Error in scraper scheduler tick");
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private async Task RunTickAsync(CancellationToken cancellationToken)
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider
                .GetRequiredService<IAmazonScraperService>()
                .ProcessScheduledSearchesAsync(cancellationToken);
        }
    }
}
