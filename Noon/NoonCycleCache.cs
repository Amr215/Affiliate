namespace Affiliate.Noon
{
    public sealed class NoonCycleSnapshot
    {
        public DateTime CompletedAtUtc { get; init; }
        public double DurationSeconds { get; init; }
        public int Brands { get; init; }
        public int CompleteBrands { get; init; }
        public int IncompleteBrands { get; init; }
        public int Products { get; init; }

        /// <summary>Pages fetched successfully (one per page, after retries).</summary>
        public int SuccessPages { get; init; }

        /// <summary>Failed page attempts (blocked / timeout / transport / no catalog data), including retries.</summary>
        public int FailedPages { get; init; }

        public int Page1Success { get; init; }
    }

    /// <summary>In-memory list of recent Noon cycle summaries (lost on process restart).</summary>
    public sealed class NoonCycleCache
    {
        private static readonly TimeSpan MaxRetention = TimeSpan.FromMinutes(15);

        private readonly object _gate = new();
        private readonly List<NoonCycleSnapshot> _items = [];

        public void Record(NoonCycleSnapshot snapshot)
        {
            lock (_gate)
            {
                _items.Add(snapshot);
                _items.RemoveAll(c => c.CompletedAtUtc < DateTime.UtcNow - MaxRetention);
            }
        }

        /// <summary>Cycles completed within the last <paramref name="window"/>, newest first.</summary>
        public IReadOnlyList<NoonCycleSnapshot> GetRecent(TimeSpan window)
        {
            var cutoff = DateTime.UtcNow - window;
            lock (_gate)
                return _items.Where(c => c.CompletedAtUtc >= cutoff).OrderByDescending(c => c.CompletedAtUtc).ToList();
        }
    }
}
