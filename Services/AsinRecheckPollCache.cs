namespace Affiliate.Services
{
    /// <summary>One completed ASIN recheck poll cycle (batch + Amazon page request stats).</summary>
    public sealed class AsinRecheckPollSnapshot
    {
        public DateTime StartedAtUtc { get; init; }
        public DateTime CompletedAtUtc { get; init; }
        public double DurationSeconds { get; init; }
        public int AsinsRequested { get; init; }
        public int BatchCount { get; init; }
        public int SuccessBatches { get; init; }
        public int FailedBatches { get; init; }
        public int IncompleteBatches { get; init; }

        /// <summary>Successful Amazon search page fetches (after retries; one per page that OK'd).</summary>
        public int SuccessPageRequests { get; init; }

        /// <summary>Failed Amazon page attempts (rejected / transport), including retries.</summary>
        public int FailedPageRequests { get; init; }

        public int Page1Success { get; init; }
        public int Page2Success { get; init; }

        public int FailBatches => FailedBatches + IncompleteBatches;
    }

    /// <summary>Thread-safe counters for Amazon page HTTP attempts within one poll.</summary>
    public sealed class AsinRecheckPageRequestCounters
    {
        private int _success;
        private int _failed;
        private int _page1Success;
        private int _page2Success;

        public int Success => _success;
        public int Failed => _failed;
        public int Page1Success => _page1Success;
        public int Page2Success => _page2Success;

        public void RecordSuccess(int page)
        {
            Interlocked.Increment(ref _success);
            if (page == 1)
                Interlocked.Increment(ref _page1Success);
            else if (page == 2)
                Interlocked.Increment(ref _page2Success);
        }

        public void RecordFailure() => Interlocked.Increment(ref _failed);
    }

    public interface IAsinRecheckPollCache
    {
        void Record(AsinRecheckPollSnapshot snapshot);

        /// <summary>Polls completed within the last <paramref name="window"/>, newest first.</summary>
        IReadOnlyList<AsinRecheckPollSnapshot> GetRecent(TimeSpan window);
    }

    /// <summary>In-memory ring of recent ASIN recheck poll summaries (lost on process restart).</summary>
    public sealed class AsinRecheckPollCache : IAsinRecheckPollCache
    {
        private static readonly TimeSpan MaxRetention = TimeSpan.FromMinutes(15);

        private readonly object _gate = new();
        private readonly List<AsinRecheckPollSnapshot> _items = [];

        public void Record(AsinRecheckPollSnapshot snapshot)
        {
            lock (_gate)
            {
                _items.Add(snapshot);
                PruneUnlocked(DateTime.UtcNow - MaxRetention);
            }
        }

        public IReadOnlyList<AsinRecheckPollSnapshot> GetRecent(TimeSpan window)
        {
            var cutoff = DateTime.UtcNow - window;
            lock (_gate)
            {
                PruneUnlocked(DateTime.UtcNow - MaxRetention);
                return _items
                    .Where(p => p.CompletedAtUtc >= cutoff)
                    .OrderByDescending(p => p.CompletedAtUtc)
                    .ToList();
            }
        }

        private void PruneUnlocked(DateTime cutoff)
        {
            _items.RemoveAll(p => p.CompletedAtUtc < cutoff);
        }
    }
}
