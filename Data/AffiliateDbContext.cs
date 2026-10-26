using Affiliate.Models;
using Affiliate.Noon.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Affiliate.Data
{
    public class AffiliateDbContext : IdentityDbContext<IdentityUser>
    {
        public AffiliateDbContext(DbContextOptions<AffiliateDbContext> options) : base(options)
        {
        }

        public DbSet<Product> Products { get; set; }
        public DbSet<PriceHistory> PriceHistories { get; set; }
        public DbSet<ScraperUrl> ScraperUrls { get; set; }
        public DbSet<OxylabsRequestLog> OxylabsRequestLogs { get; set; }
        public DbSet<NoonBrandUrl> NoonBrandUrls { get; set; }
        public DbSet<NoonProduct> NoonProducts { get; set; }
        public DbSet<NoonPriceHistory> NoonPriceHistories { get; set; }
        public DbSet<NoonRequestLog> NoonRequestLogs { get; set; }
        public DbSet<ProductLink> ProductLinks { get; set; }
        public DbSet<AmazonShortLink> AmazonShortLinks { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<ScraperUrl>(entity =>
            {
                entity.ToTable("ScraperUrls");
                entity.Property(s => s.Name).HasMaxLength(200);
                entity.Property(s => s.Url).HasMaxLength(4000);
                entity.Property(s => s.Domain).HasMaxLength(16);
                entity.Property(s => s.DropAlertPercent).HasPrecision(5, 2);
            });

            modelBuilder.Entity<Product>(entity =>
            {
                entity.Property(p => p.CurrentPrice).HasPrecision(18, 2);
                entity.Property(p => p.LowestPrice).HasPrecision(18, 2);
                entity.Property(p => p.HighestPrice).HasPrecision(18, 2);
                entity.Property(p => p.DropPercent).HasPrecision(18, 2);
                entity.Property(p => p.DropBaselinePrice).HasPrecision(18, 2);
                entity.Property(p => p.LastDropAlertPercent).HasPrecision(18, 2);

                // ASIN is the natural product key and the scraper's hot lookup path.
                entity.Property(p => p.Asin).HasMaxLength(16);
                entity.HasIndex(p => p.Asin)
                    .IsUnique()
                    .HasFilter("[Asin] IS NOT NULL");

                // Speeds ASIN recheck: filter available products, order by oldest LastCheckedAt.
                entity.HasIndex(p => new { p.IsAvailable, p.LastCheckedAt });

                entity.HasIndex(p => p.ScraperUrlId);
                entity.HasOne(p => p.ScraperUrl)
                    .WithMany(s => s.Products)
                    .HasForeignKey(p => p.ScraperUrlId)
                    .IsRequired(false)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<OxylabsRequestLog>(entity =>
            {
                entity.Property(l => l.StatusPhrase).HasMaxLength(64);
                entity.HasIndex(l => l.ScraperUrlId);
                entity.HasIndex(l => l.RequestedAt);
                entity.HasIndex(l => l.Port);
                entity.HasIndex(l => new { l.ScraperUrlId, l.RequestedAt });

                entity.HasOne(l => l.ScraperUrl)
                    .WithMany(s => s.OxylabsRequestLogs)
                    .HasForeignKey(l => l.ScraperUrlId)
                    .IsRequired(false)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // Configure relationships
            modelBuilder.Entity<PriceHistory>()
                .HasOne(ph => ph.Product)
                .WithMany(p => p.PriceHistory)
                .HasForeignKey(ph => ph.ProductId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<NoonBrandUrl>(entity =>
            {
                entity.Property(b => b.Name).HasMaxLength(200);
                entity.Property(b => b.Url).HasMaxLength(4000);
                entity.Property(b => b.DropAlertPercent).HasPrecision(5, 2);
            });

            modelBuilder.Entity<NoonProduct>(entity =>
            {
                entity.Property(p => p.Sku).HasMaxLength(32);
                entity.HasIndex(p => p.Sku).IsUnique();
                entity.Property(p => p.Currency).HasMaxLength(8);
                entity.Property(p => p.CurrentPrice).HasPrecision(18, 2);
                entity.Property(p => p.OriginalPrice).HasPrecision(18, 2);
                entity.Property(p => p.LowestPrice).HasPrecision(18, 2);
                entity.Property(p => p.HighestPrice).HasPrecision(18, 2);
                entity.Property(p => p.DropPercent).HasPrecision(18, 2);
                entity.Property(p => p.DropBaselinePrice).HasPrecision(18, 2);
                entity.Property(p => p.LastDropAlertPercent).HasPrecision(18, 2);

                entity.HasOne(p => p.NoonBrandUrl)
                    .WithMany(b => b.Products)
                    .HasForeignKey(p => p.NoonBrandUrlId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<NoonPriceHistory>(entity =>
            {
                entity.Property(h => h.Price).HasPrecision(18, 2);
                entity.Property(h => h.OriginalPrice).HasPrecision(18, 2);
                entity.HasOne(h => h.NoonProduct)
                    .WithMany(p => p.PriceHistory)
                    .HasForeignKey(h => h.NoonProductId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<NoonRequestLog>(entity =>
            {
                entity.Property(l => l.Proxy).HasMaxLength(64);
                entity.Property(l => l.RequestUrl).HasMaxLength(4000);
                entity.HasIndex(l => l.RequestedAt);
                entity.HasIndex(l => new { l.NoonBrandUrlId, l.RequestedAt });
                entity.HasOne(l => l.NoonBrandUrl)
                    .WithMany()
                    .HasForeignKey(l => l.NoonBrandUrlId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<ProductLink>(entity =>
            {
                entity.HasIndex(l => l.ProductId).IsUnique();
                entity.HasIndex(l => l.NoonProductId).IsUnique();
                entity.HasOne(l => l.Product)
                    .WithMany()
                    .HasForeignKey(l => l.ProductId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(l => l.NoonProduct)
                    .WithMany()
                    .HasForeignKey(l => l.NoonProductId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<AmazonShortLink>(entity =>
            {
                entity.Property(l => l.Code).HasMaxLength(16);
                entity.Property(l => l.DestinationUrl).HasMaxLength(4000);
                entity.HasIndex(l => l.Code).IsUnique();
            });
        }
    }
}
