using Microsoft.Extensions.Options;

namespace Affiliate.Noon
{
    /// <summary>
    /// Noon's own proxy block state, kept apart from <see cref="Services.IIspProxyService"/>
    /// so a proxy Noon rejects is still used for Amazon (and the other way round).
    /// </summary>
    public sealed class NoonProxyBlocklist(IOptions<NoonOptions> options)
    {
        private readonly NoonOptions _options = options.Value;
        private readonly object _gate = new();
        private readonly Dictionary<string, (int Failures, DateTime? BlockedUntil)> _states = new();

        public bool IsBlocked(string key)
        {
            lock (_gate)
                return _states.TryGetValue(key, out var s) && s.BlockedUntil > DateTime.UtcNow;
        }

        /// <summary>Consecutive failures and, while blocked, when the block ends.</summary>
        public (int Failures, DateTime? BlockedUntil) GetState(string key)
        {
            lock (_gate)
            {
                if (!_states.TryGetValue(key, out var s))
                    return (0, null);
                return s.BlockedUntil > DateTime.UtcNow ? s : (s.Failures, null);
            }
        }

        public void ReportSuccess(string key)
        {
            lock (_gate)
                _states.Remove(key);
        }

        /// <summary>Returns true when this failure blocked the proxy.</summary>
        public bool ReportFailure(string key)
        {
            lock (_gate)
            {
                _states.TryGetValue(key, out var s);
                var failures = s.Failures + 1;
                if (failures < Math.Max(1, _options.ConsecutiveFailuresBeforeBlock))
                {
                    _states[key] = (failures, null);
                    return false;
                }

                _states[key] = (0, DateTime.UtcNow.AddSeconds(Math.Max(1, _options.BlockDurationSeconds)));
                return true;
            }
        }
    }
}
