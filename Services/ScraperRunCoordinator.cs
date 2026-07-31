namespace Affiliate.Services
{
    /// <summary>Ensures only one Oxylabs search runs at a time.</summary>
    public interface IScraperRunCoordinator
    {
        /// <summary>Tries to take the lock immediately. Returns false if another scrape holds it.</summary>
        Task<bool> TryEnterAsync(CancellationToken cancellationToken = default);

        /// <summary>Waits until the lock is free, then takes it.</summary>
        Task WaitEnterAsync(CancellationToken cancellationToken = default);

        void Release();
    }

    public sealed class ScraperRunCoordinator : IScraperRunCoordinator, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public Task<bool> TryEnterAsync(CancellationToken cancellationToken = default) =>
            _gate.WaitAsync(0, cancellationToken);

        public Task WaitEnterAsync(CancellationToken cancellationToken = default) =>
            _gate.WaitAsync(cancellationToken);

        public void Release()
        {
            if (_gate.CurrentCount == 0)
                _gate.Release();
        }

        public void Dispose() => _gate.Dispose();
    }
}
