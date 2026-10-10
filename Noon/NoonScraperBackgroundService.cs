using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Affiliate.Noon
{
    /// <summary>Runs Noon brand cycles back to back, like the ASIN recheck scheduler (target cycle length + cool-downs).</summary>
    public sealed class NoonScraperBackgroundService(
        IServiceProvider services,
        IOptions<NoonOptions> options,
        ILogger<NoonScraperBackgroundService> logger) : BackgroundService
    {
        private readonly NoonOptions _options = options.Value;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                logger.LogInformation("Noon scraper disabled (Noon:Enabled = false)");
                return;
            }

            var min = Math.Max(1, _options.PollIntervalMinSeconds);
            var max = Math.Max(min, _options.PollIntervalMaxSeconds);
            var minimumGap = TimeSpan.FromSeconds(Math.Max(1, _options.MinimumGapSeconds));
            var coolDownEvery = TimeSpan.FromMinutes(Math.Max(0, _options.CoolDownEveryMinutes));
            var coolDownMin = Math.Max(1, _options.CoolDownMinSeconds);
            var coolDownMax = Math.Max(coolDownMin, _options.CoolDownMaxSeconds);
            var activeSinceCoolDown = Stopwatch.StartNew();

            logger.LogInformation(
                "Noon scraper started (cycle every {Min}-{Max}s, {Parallel} brands in parallel)",
                min, max, _options.MaxParallelBrands);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (coolDownEvery > TimeSpan.Zero && activeSinceCoolDown.Elapsed >= coolDownEvery)
                    {
                        var seconds = Random.Shared.Next(coolDownMin, coolDownMax + 1);
                        logger.LogInformation("Noon cool-down: pausing {Seconds}s", seconds);
                        await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                        activeSinceCoolDown.Restart();
                    }

                    var target = TimeSpan.FromSeconds(Random.Shared.Next(min, max + 1));
                    var started = Stopwatch.StartNew();

                    await RunCycleAsync(stoppingToken);

                    var delay = target - started.Elapsed;
                    if (delay < minimumGap)
                        delay = minimumGap;

                    await Task.Delay(delay, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }

        private async Task RunCycleAsync(CancellationToken ct)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<INoonScraperService>().ProcessCycleAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in Noon scraper cycle");
            }
        }
    }
}
