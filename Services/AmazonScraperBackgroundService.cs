namespace Affiliate.Services
{
    public sealed class AmazonScraperBackgroundService(
        IServiceProvider services,
        ILogger<AmazonScraperBackgroundService> logger) : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "Amazon scraper scheduler started (poll every {Interval}; intervals from ScraperSearches).",
                PollInterval);

            using var timer = new PeriodicTimer(PollInterval);

            do
            {
                await RunTickAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task RunTickAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<IAmazonScraperService>()
                    .ProcessScheduledSearchesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in scraper scheduler tick");
            }
        }
    }
}
