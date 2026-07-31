using System.Diagnostics;
using Affiliate.Options;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    /// <summary>
    /// Separate scheduler for ASIN re-checks via ISP proxy (independent of URL scrape polling).
    /// </summary>
    public sealed class AsinRecheckBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly AsinRecheckOptions _options;
        private readonly ILogger<AsinRecheckBackgroundService> _logger;

        public AsinRecheckBackgroundService(
            IServiceProvider services,
            IOptions<AsinRecheckOptions> options,
            ILogger<AsinRecheckBackgroundService> logger)
        {
            _services = services;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var min = Math.Max(1, _options.PollIntervalMinSeconds);
            var max = Math.Max(min, _options.PollIntervalMaxSeconds);
            var minimumGap = TimeSpan.FromSeconds(Math.Max(1, _options.MinimumGapSeconds));

            _logger.LogInformation(
                "ASIN recheck scheduler started (cycle every {Min}-{Max}s; Enabled={Enabled}, BatchSize={BatchSize}, AsinsPerPoll={AsinsPerPoll}, MaxParallelBatches={Parallel})",
                min, max, _options.Enabled, _options.BatchSize, _options.AsinsPerPoll, _options.MaxParallelBatches);

            while (!stoppingToken.IsCancellationRequested)
            {
                var startedAt = Stopwatch.GetTimestamp();
                var target = TimeSpan.FromSeconds(Random.Shared.Next(min, max + 1));

                await RunTickAsync(stoppingToken);

                // The cycle length is the target, so the work itself counts against the interval.
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                var delay = target - elapsed;

                if (delay < minimumGap)
                {
                    _logger.LogWarning(
                        "ASIN recheck cycle took {Elapsed:0.0}s, over the {Target:0}s target — falling behind the requested rate",
                        elapsed.TotalSeconds, target.TotalSeconds);
                    delay = minimumGap;
                }

                _logger.LogDebug(
                    "ASIN recheck: tick took {Elapsed:0.0}s, next poll in {Seconds:0.0}s",
                    elapsed.TotalSeconds, delay.TotalSeconds);

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
                await using var scope = _services.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<IAmazonScraperService>()
                    .ProcessAsinRecheckAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in ASIN recheck scheduler tick");
            }
        }
    }
}
