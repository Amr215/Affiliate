using Affiliate.Options;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    /// <summary>
    /// Runs <see cref="IProductDescriptionFillService"/> on a fixed interval (default every 24 hours).
    /// Independent of ASIN recheck, URL scrape, and Arabic name fill schedulers.
    /// </summary>
    public sealed class ProductDescriptionFillBackgroundService(
        IServiceProvider services,
        IOptions<ProductDescriptionFillOptions> options,
        ILogger<ProductDescriptionFillBackgroundService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var opts = options.Value;
            var interval = TimeSpan.FromHours(Math.Max(1, opts.IntervalHours));
            var startupDelay = TimeSpan.FromSeconds(Math.Max(0, opts.StartupDelaySeconds));

            logger.LogWarning(
                "Product description fill scheduler STARTED (Enabled={Enabled}, every {Hours}h, MaxProducts={Max}, startup delay {Startup}s)",
                opts.Enabled, interval.TotalHours, opts.MaxProductsPerRun, startupDelay.TotalSeconds);

            if (startupDelay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(startupDelay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                await RunTickAsync(stoppingToken);

                var elapsed = DateTime.UtcNow - started;
                var delay = interval - elapsed;
                if (delay < TimeSpan.FromMinutes(1))
                    delay = TimeSpan.FromMinutes(1);

                logger.LogInformation(
                    "Product description fill: next run in {Hours:0.##}h",
                    delay.TotalHours);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private async Task RunTickAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<IProductDescriptionFillService>()
                    .ProcessAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in product description fill scheduler tick");
            }
        }
    }
}
