using System.Collections.Concurrent;

namespace Affiliate.Services
{
    /// <summary>One in-flight scrape per <see cref="Affiliate.Models.ScraperUrl"/> — different URLs can run in parallel.</summary>
    public interface IScraperRunCoordinator
    {
        /// <summary>Tries to take the lock for this URL immediately. Returns false if it is already scraping.</summary>
        Task<bool> TryEnterAsync(int scraperUrlId, CancellationToken cancellationToken = default);

        void Release(int scraperUrlId);
    }

    public sealed class ScraperRunCoordinator : IScraperRunCoordinator
    {
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _urlGates = new();

        private SemaphoreSlim Gate(int scraperUrlId) =>
            _urlGates.GetOrAdd(scraperUrlId, _ => new SemaphoreSlim(1, 1));

        public Task<bool> TryEnterAsync(int scraperUrlId, CancellationToken cancellationToken = default) =>
            Gate(scraperUrlId).WaitAsync(0, cancellationToken);

        public void Release(int scraperUrlId)
        {
            var gate = Gate(scraperUrlId);
            if (gate.CurrentCount == 0)
                gate.Release();
        }
    }
}