using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Models;
using SentinelMfgSuite.Web.Models.Assessments;
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
        public DbSet<AssessmentReport> AssessmentReports { get; set; } = null!;
        public DbSet<AssessmentReportRevision> AssessmentReportRevisions { get; set; } = null!;

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

            // Assessment domain
            modelBuilder.Entity<Assessment>(b =>
            {
                b.HasKey(x => x.Id);

                b.Property(x => x.Title).HasMaxLength(200);
                b.Property(x => x.SiteName).HasMaxLength(200);
                b.Property(x => x.ContactEmail).HasMaxLength(320);

                // store enum as smallint
                b.Property(x => x.Status).HasColumnType("smallint").IsRequired();

                // UTC timestamps as timestamptz
                b.Property(x => x.CreatedUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.UpdatedUtc).HasColumnType("timestamp with time zone");

                b.Property(x => x.Notes).HasMaxLength(2000);
                b.Property(x => x.OwnerUserId).HasMaxLength(200);

                b.Property(x => x.QuestionnaireVersion)
                    .IsRequired()
                    .HasMaxLength(20)
                    .HasDefaultValue("1.0");
            });

            modelBuilder.Entity<AssessmentInvitation>(b =>
            {
                b.HasKey(x => x.Id);

                b.Property(x => x.Email).IsRequired().HasMaxLength(320);
                b.Property(x => x.TokenHash).IsRequired().HasMaxLength(200);

                b.Property(x => x.CreatedUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.ExpiresUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.RevokedUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.LastAccessUtc).HasColumnType("timestamp with time zone");

                b.Property(x => x.DeliveryStatus).HasColumnType("smallint").HasDefaultValue(AssessmentInvitationDeliveryStatus.Pending).IsRequired();
                b.Property(x => x.DeliveryAttemptCount).HasDefaultValue(0).IsRequired();
                b.Property(x => x.DeliveryLastAttemptUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.DeliveryAcceptedUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.DeliveryFailureCode).HasMaxLength(100);

                // unique token hash for fast lookup
                b.HasIndex(x => x.TokenHash).IsUnique();

                // assessment FK - restrict deletes
                b.HasOne<Assessment>()
                    .WithMany()
                    .HasForeignKey(x => x.AssessmentId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<AssessmentResponse>(b =>
            {
                b.HasKey(x => x.Id);

                // store response body as jsonb
                b.Property(x => x.Data).HasColumnType("jsonb").IsRequired();

                b.Property(x => x.IsDraft).IsRequired();

                b.Property(x => x.LastSavedUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.SubmittedUtc).HasColumnType("timestamp with time zone");

                // concurrency token
                b.Property(x => x.ConcurrencyStamp).IsConcurrencyToken().HasMaxLength(100);

                // One response per invitation
                b.HasIndex(x => x.InvitationId).IsUnique();
                b.HasIndex(x => x.AssessmentId);

                // foreign keys - restrict deletes
                b.HasOne<Assessment>()
                    .WithMany()
                    .HasForeignKey(x => x.AssessmentId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne<AssessmentInvitation>()
                    .WithOne()
                    .HasForeignKey<AssessmentResponse>(x => x.InvitationId)
                    .OnDelete(DeleteBehavior.Restrict);

            });

            modelBuilder.Entity<AssessmentReport>(b =>
            {
                b.HasKey(x => x.Id);

                b.Property(x => x.QuestionnaireVersion).IsRequired().HasMaxLength(20);
                b.Property(x => x.ResponseSubmittedUtc).HasColumnType("timestamp with time zone").IsRequired();
                b.Property(x => x.CreatedUtc).HasColumnType("timestamp with time zone").IsRequired();
                b.Property(x => x.UpdatedUtc).HasColumnType("timestamp with time zone").IsRequired();
                b.Property(x => x.CreatedByUserId).IsRequired().HasMaxLength(200);
                b.Property(x => x.UpdatedByUserId).IsRequired().HasMaxLength(200);
                b.Property(x => x.ConcurrencyStamp).IsConcurrencyToken().IsRequired();

                b.HasIndex(x => x.SubmittedResponseId).IsUnique();
                b.HasIndex(x => x.AssessmentId);

                b.HasOne(x => x.Assessment)
                    .WithMany()
                    .HasForeignKey(x => x.AssessmentId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne(x => x.SubmittedResponse)
                    .WithMany()
                    .HasForeignKey(x => x.SubmittedResponseId)
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasMany(x => x.Revisions)
                    .WithOne(x => x.AssessmentReport)
                    .HasForeignKey(x => x.AssessmentReportId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<AssessmentReportRevision>(b =>
            {
                b.HasKey(x => x.Id);

                b.Property(x => x.Status).HasColumnType("smallint").IsRequired();
                b.Property(x => x.ExecutiveSummary).HasColumnType("text").IsRequired();
                b.Property(x => x.ScopeAndMethodology).HasColumnType("text").IsRequired();
                b.Property(x => x.AssumptionsAndLimitations).HasColumnType("text").IsRequired();
                b.Property(x => x.NextSteps).HasColumnType("text").IsRequired();
                b.Property(x => x.ScoringContractVersion).HasMaxLength(50);
                b.Property(x => x.ScoringSnapshot).HasColumnType("jsonb");
                b.Property(x => x.ConsultantContentJson).HasColumnType("jsonb");
                b.Property(x => x.ConsultantContentSchemaVersion).HasMaxLength(20);
                b.Property(x => x.ReportTemplateVersion).HasMaxLength(50);
                b.Property(x => x.CreatedUtc).HasColumnType("timestamp with time zone").IsRequired();
                b.Property(x => x.UpdatedUtc).HasColumnType("timestamp with time zone").IsRequired();
                b.Property(x => x.CreatedByUserId).IsRequired().HasMaxLength(200);
                b.Property(x => x.UpdatedByUserId).IsRequired().HasMaxLength(200);
                b.Property(x => x.SubmittedForReviewUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.SubmittedForReviewByUserId).HasMaxLength(200);
                b.Property(x => x.ApprovedUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.ApprovedByUserId).HasMaxLength(200);
                b.Property(x => x.IssuedUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.IssuedByUserId).HasMaxLength(200);
                b.Property(x => x.PdfStorageKey).HasMaxLength(500);
                b.Property(x => x.PdfSha256).HasMaxLength(64);
                b.Property(x => x.PdfLengthBytes);
                b.Property(x => x.ConcurrencyStamp).IsConcurrencyToken().IsRequired();

                b.HasIndex(x => new { x.AssessmentReportId, x.RevisionNumber }).IsUnique();
                b.ToTable(table => table.HasCheckConstraint("CK_AssessmentReportRevision_RevisionNumber_Positive", "\"RevisionNumber\" > 0"));
            });

        }
    }
}
