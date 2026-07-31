using Affiliate.Options;
using Microsoft.Extensions.Options;

namespace Affiliate.Services
{
    /// <summary>
    /// Separate scheduler for ASIN batch re-checks (independent of keyword search polling).
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
            var seconds = _options.PollIntervalSeconds > 0 ? _options.PollIntervalSeconds : 3600;
            var interval = TimeSpan.FromSeconds(seconds);

            _logger.LogInformation(
                "ASIN recheck scheduler started (poll every {Interval}; Enabled={Enabled}, AsinsPerPoll={AsinsPerPoll}, BatchSize={BatchSize})",
                interval, _options.Enabled, _options.AsinsPerPoll, _options.BatchSize);

            using var timer = new PeriodicTimer(interval);

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
