namespace Affiliate.ViewModels
{
    public class HomeDashboardViewModel
    {
        public int AmazonProducts { get; init; }
        public int AmazonAvailable { get; init; }
        public int AmazonUrls { get; init; }

        public int NoonProducts { get; init; }
        public int NoonAvailable { get; init; }
        public int NoonBrands { get; init; }
        public int NoonRequestsLastHour { get; init; }
        public int NoonFailedLastHour { get; init; }

        public int Links { get; init; }
    }
}
