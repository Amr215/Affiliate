using Affiliate.Identity;
using Affiliate.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Affiliate.Data
{
    public static class IdentitySeeder
    {
        public static async Task SeedAsync(IServiceProvider services)
        {
            var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = services.GetRequiredService<UserManager<IdentityUser>>();
            var seedOptions = services.GetRequiredService<IOptions<AdminSeedOptions>>().Value;
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("IdentitySeeder");

            if (!await roleManager.RoleExistsAsync(AppRoles.Admin))
            {
                var roleResult = await roleManager.CreateAsync(new IdentityRole(AppRoles.Admin));
                if (!roleResult.Succeeded)
                {
                    logger.LogError(
                        "Failed to create Admin role: {Errors}",
                        string.Join(", ", roleResult.Errors.Select(e => e.Description)));
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(seedOptions.Email) || string.IsNullOrWhiteSpace(seedOptions.Password))
            {
                logger.LogWarning("AdminSeed Email/Password is not configured; skipping admin user seed.");
                return;
            }

            var existing = await userManager.FindByEmailAsync(seedOptions.Email);
            if (existing != null)
                return;

            var admin = new IdentityUser
            {
                UserName = seedOptions.Email,
                Email = seedOptions.Email,
                EmailConfirmed = true
            };

            var createResult = await userManager.CreateAsync(admin, seedOptions.Password);
            if (!createResult.Succeeded)
            {
                logger.LogError(
                    "Failed to create seed admin user: {Errors}",
                    string.Join(", ", createResult.Errors.Select(e => e.Description)));
                return;
            }

            var roleAssign = await userManager.AddToRoleAsync(admin, AppRoles.Admin);
            if (!roleAssign.Succeeded)
            {
                logger.LogError(
                    "Failed to assign Admin role to seed user: {Errors}",
                    string.Join(", ", roleAssign.Errors.Select(e => e.Description)));
            }
            else
            {
                logger.LogInformation("Seeded admin user {Email}", seedOptions.Email);
            }
        }
    }
}
