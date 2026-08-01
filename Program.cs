using Affiliate.Data;
using Affiliate.Identity;
using Affiliate.Options;
using Affiliate.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews(options =>
{
    var policy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole(AppRoles.Admin)
        .Build();
    options.Filters.Add(new AuthorizeFilter(policy));
});

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "RequestVerificationToken";
});

builder.Services.Configure<TelegramOptions>(
    builder.Configuration.GetSection(TelegramOptions.SectionName));

builder.Services.Configure<AsinRecheckOptions>(
    builder.Configuration.GetSection(AsinRecheckOptions.SectionName));

builder.Services.Configure<IspProxyOptions>(
    builder.Configuration.GetSection(IspProxyOptions.SectionName));

builder.Services.Configure<KeepAliveOptions>(
    builder.Configuration.GetSection(KeepAliveOptions.SectionName));

builder.Services.Configure<AdminSeedOptions>(_ => { });
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                       ?? "Server=(localdb)\\mssqllocaldb;Database=AffiliateDb;Trusted_Connection=true;";

builder.Services.AddDbContext<AffiliateDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services
    .AddIdentity<IdentityUser, IdentityRole>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.User.RequireUniqueEmail = true;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<AffiliateDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
});

builder.Services.AddHttpClient(TelegramNotifier.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient(KeepAliveBackgroundService.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddSingleton<KeepAliveUrlStore>();
builder.Services.AddSingleton<IScraperRunCoordinator, ScraperRunCoordinator>();
builder.Services.AddSingleton<IIspProxyRoundRobin, IspProxyRoundRobin>();
builder.Services.AddScoped<ITelegramNotifier, TelegramNotifier>();
builder.Services.AddScoped<IAmazonScraperService, AmazonScraperService>();
builder.Services.AddHostedService<AmazonScraperBackgroundService>();
builder.Services.AddHostedService<AsinRecheckBackgroundService>();
builder.Services.AddHostedService<KeepAliveBackgroundService>();
builder.Services.AddHostedService<OxylabsRequestLogCleanupService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AffiliateDbContext>();
    dbContext.Database.Migrate();
    await IdentitySeeder.SeedAsync(scope.ServiceProvider);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    context.RequestServices.GetRequiredService<KeepAliveUrlStore>()
        .TrySetFromRequest(context.Request.Scheme, context.Request.Host);
    await next();
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Text("Healthy", "text/plain"))
    .AllowAnonymous()
    .DisableAntiforgery();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
