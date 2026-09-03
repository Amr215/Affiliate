using Affiliate.Services;

namespace Affiliate.ViewModels
{
    public class AsinRecheckPollReportViewModel
    {
        public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);
        public IReadOnlyList<AsinRecheckPollSnapshot> Polls { get; set; } = [];

        public int PollCount => Polls.Count;
        public int TotalBatches => Polls.Sum(p => p.BatchCount);
        public int TotalSuccessBatches => Polls.Sum(p => p.SuccessBatches);
        public int TotalFailedBatches => Polls.Sum(p => p.FailBatches);
        public int TotalSuccessPages => Polls.Sum(p => p.SuccessPageRequests);
        public int TotalFailedPages => Polls.Sum(p => p.FailedPageRequests);
        public int TotalPage1Success => Polls.Sum(p => p.Page1Success);
        public int TotalPage2Success => Polls.Sum(p => p.Page2Success);
        public double AvgDurationSeconds =>
            Polls.Count == 0 ? 0 : Polls.Average(p => p.DurationSeconds);

        public double PageSuccessRate
        {
            get
            {
                var total = TotalSuccessPages + TotalFailedPages;
                return total == 0 ? 0 : 100.0 * TotalSuccessPages / total;
            }
        }
    }
}
