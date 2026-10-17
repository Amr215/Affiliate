namespace Affiliate.Options
{
    public class AdminAccessOptions
    {
        public const string SectionName = "AdminAccess";

        /// <summary>
        /// Private path for the admin login page (must start with <c>/</c>).
        /// Unauthenticated visitors to any other page get 404 instead of a login redirect.
        /// </summary>
        public string SecretLoginPath { get; set; } = "/a9f2c7e1/login";
    }
}
