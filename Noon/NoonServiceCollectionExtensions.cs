namespace Affiliate.Noon
{
    public static class NoonServiceCollectionExtensions
    {
        public static IServiceCollection AddNoon(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<NoonOptions>(configuration.GetSection(NoonOptions.SectionName));
            services.AddSingleton<NoonProxyBlocklist>();
            services.AddSingleton<NoonCycleCache>();
            services.AddSingleton<NoonCatalogClient>();
            services.AddScoped<INoonScraperService, NoonScraperService>();
            services.AddHostedService<NoonScraperBackgroundService>();
            services.AddHostedService<NoonRequestLogCleanupService>();
            return services;
        }
    }
}
