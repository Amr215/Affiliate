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
            services.AddSingleton<NoonPublishDraftCache>();
            services.AddSingleton<NoonPublishAuditLogger>();
            services.AddSingleton<INoonPublishAuditLogger>(sp => sp.GetRequiredService<NoonPublishAuditLogger>());
            services.AddSingleton<NoonTelegramPublishHandler>();
            services.AddSingleton<NoonSkuLinkCodec>();
            services.AddScoped<INoonPrepareForPublishService, NoonPrepareForPublishService>();
            services.AddScoped<NoonShortLinkService>();
            services.AddScoped<INoonScraperService, NoonScraperService>();
            services.AddHostedService<NoonScraperBackgroundService>();
            services.AddHostedService<NoonRequestLogCleanupService>();
            services.AddHostedService<NoonPublishAuditFlushService>();
            return services;
        }
    }
}
