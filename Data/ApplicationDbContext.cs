using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Models;
using SentinelMfgSuite.Web.Models.Tenants;

namespace SentinelMfgSuite.Web.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<WebLead> WebLeads { get; set; } = null!;

        // ✅ Tenant core
        public DbSet<Tenant> Tenants { get; set; } = null!;
        public DbSet<TenantSubscription> TenantSubscriptions { get; set; } = null!;
        public DbSet<TenantUserRole> TenantUserRoles { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<WebLead>(b =>
            {
                b.Property(x => x.Email).IsRequired().HasMaxLength(320);
                b.Property(x => x.Name).HasMaxLength(200);
                b.Property(x => x.Company).HasMaxLength(200);
                b.Property(x => x.Role).HasMaxLength(200);
                b.Property(x => x.Topic).HasMaxLength(100);
                b.Property(x => x.SourcePage).HasMaxLength(400);
                b.Property(x => x.Referrer).HasMaxLength(400);
                b.Property(x => x.UtmSource).HasMaxLength(100);
                b.Property(x => x.UtmMedium).HasMaxLength(100);
                b.Property(x => x.UtmCampaign).HasMaxLength(100);
                b.Property(x => x.UtmContent).HasMaxLength(100);
                b.Property(x => x.UtmTerm).HasMaxLength(100);
            });

            // ✅ Tenant configs
            modelBuilder.Entity<Tenant>(b =>
            {
                b.Property(x => x.Name).IsRequired().HasMaxLength(200);
            });

            modelBuilder.Entity<TenantSubscription>(b =>
            {
                b.Property(x => x.Tier).IsRequired().HasMaxLength(50);
                b.Property(x => x.BillingCadence).IsRequired().HasMaxLength(50);

                b.Property(x => x.StripeCustomerId).HasMaxLength(200);
                b.Property(x => x.StripeSubscriptionId).HasMaxLength(200);

                b.HasOne(x => x.Tenant)
                    .WithMany()                 // later: Tenant.Subscriptions
                    .HasForeignKey(x => x.TenantId)
                    .OnDelete(DeleteBehavior.Cascade);

                b.HasIndex(x => x.TenantId).IsUnique();    // good lookup
            });


            modelBuilder.Entity<TenantUserRole>(b =>
            {
                b.HasKey(x => new { x.TenantId, x.UserId, x.Role });
            });
        }
    }
}
