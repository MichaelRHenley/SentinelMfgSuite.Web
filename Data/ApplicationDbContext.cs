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
        public DbSet<AssessmentOrder> AssessmentOrders { get; set; } = null!;
        public DbSet<AssessmentPaymentEvent> AssessmentPaymentEvents { get; set; } = null!;
        public DbSet<AssessmentReport> AssessmentReports { get; set; } = null!;
        public DbSet<AssessmentReportRevision> AssessmentReportRevisions { get; set; } = null!;
        public DbSet<AssessmentReportTransitionHistory> AssessmentReportTransitionHistory { get; set; } = null!;

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

            modelBuilder.Entity<Assessment>().Property(x => x.PaymentPolicy)
                .HasColumnType("smallint").HasDefaultValue(AssessmentPaymentPolicy.LegacyExempt).IsRequired();

            modelBuilder.Entity<AssessmentOrder>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.CustomerOrganization).HasMaxLength(200).IsRequired();
                b.Property(x => x.BillingContactName).HasMaxLength(200).IsRequired();
                b.Property(x => x.BillingContactEmail).HasMaxLength(320).IsRequired();
                b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
                b.Property(x => x.UnitPrice).HasColumnType("numeric(12,2)");
                b.Property(x => x.TaxRate).HasColumnType("numeric(7,5)");
                b.Property(x => x.TaxAmount).HasColumnType("numeric(12,2)");
                b.Property(x => x.TotalAmount).HasColumnType("numeric(12,2)");
                b.Property(x => x.TaxLabel).HasMaxLength(50);
                b.Property(x => x.TaxTreatment).HasColumnType("smallint");
                b.Property(x => x.PaymentStatus).HasColumnType("smallint");
                b.Property(x => x.PaymentMethod).HasColumnType("smallint");
                b.Property(x => x.PaymentProvider).HasMaxLength(50);
                b.Property(x => x.ProviderPaymentReference).HasMaxLength(200);
                b.Property(x => x.PaymentReference).HasMaxLength(200);
                b.Property(x => x.VerifiedBy).HasMaxLength(200);
                b.Property(x => x.CreatedBy).HasMaxLength(200);
                b.Property(x => x.CreatedUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.UpdatedUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.VerifiedPaymentUtc).HasColumnType("timestamp with time zone");
                b.HasIndex(x => x.AssessmentId).IsUnique();
                b.HasOne<Assessment>().WithMany().HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<AssessmentPaymentEvent>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.EventType).HasColumnType("smallint");
                b.Property(x => x.FromStatus).HasColumnType("smallint");
                b.Property(x => x.ToStatus).HasColumnType("smallint");
                b.Property(x => x.ActorType).HasColumnType("smallint");
                b.Property(x => x.ActorId).HasMaxLength(200);
                b.Property(x => x.Provider).HasMaxLength(50);
                b.Property(x => x.ProviderEventId).HasMaxLength(200);
                b.Property(x => x.PaymentReference).HasMaxLength(200);
                b.Property(x => x.Metadata).HasMaxLength(1000);
                b.Property(x => x.OccurredUtc).HasColumnType("timestamp with time zone");
                b.HasIndex(x => x.OrderId);
                b.HasIndex(x => new { x.Provider, x.ProviderEventId }).IsUnique()
                    .HasFilter("\"ProviderEventId\" IS NOT NULL");
                b.HasOne<AssessmentOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
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

            modelBuilder.Entity<SentinelMfgSuite.Web.Models.Email.EmailOutboxMessage>(b =>
            {
                b.ToTable("EmailOutboxMessages");
                b.HasKey(x => x.Id);
                b.Property(x => x.BusinessEventKey).HasMaxLength(200).IsRequired();
                b.Property(x => x.Classification).HasMaxLength(64).IsRequired();
                b.Property(x => x.RecipientEmail).HasMaxLength(320).IsRequired();
                b.Property(x => x.Subject).HasMaxLength(300).IsRequired();
                b.Property(x => x.HtmlBody).IsRequired();
                b.Property(x => x.TextBody).IsRequired();
                b.Property(x => x.ProviderMessageId).HasMaxLength(200);
                b.Property(x => x.LastFailureCode).HasMaxLength(100);
                b.Property(x => x.NextAttemptUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.LockedUntilUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.CreatedUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.ProviderAcceptedUtc).HasColumnType("timestamp with time zone");
                b.HasIndex(x => x.BusinessEventKey).IsUnique();
                b.HasIndex(x => new { x.Status, x.NextAttemptUtc });
                b.HasIndex(x => x.AssessmentId);
                b.HasMany(x => x.Attempts).WithOne().HasForeignKey(a => a.OutboxMessageId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<SentinelMfgSuite.Web.Models.Email.EmailOutboxAttempt>(b =>
            {
                b.ToTable("EmailOutboxAttempts");
                b.HasKey(x => x.Id);
                b.Property(x => x.Outcome).HasMaxLength(50);
                b.Property(x => x.FailureCode).HasMaxLength(100);
                b.Property(x => x.StartedUtc).HasColumnType("timestamp with time zone");
                b.Property(x => x.CompletedUtc).HasColumnType("timestamp with time zone");
                b.HasIndex(x => new { x.OutboxMessageId, x.AttemptNumber });
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

                b.HasAlternateKey(x => new { x.AssessmentReportId, x.Id });
                b.HasIndex(x => new { x.AssessmentReportId, x.RevisionNumber }).IsUnique();

                b.Property(x => x.SupersededByRevisionId);
                b.HasOne<AssessmentReportRevision>()
                    .WithMany()
                    .HasForeignKey(x => new { x.AssessmentReportId, x.SupersededByRevisionId })
                    .HasPrincipalKey(x => new { x.AssessmentReportId, x.Id })
                    .HasConstraintName("FK_AssessmentReportRevisions_SupersededBy_SameReport")
                    .OnDelete(DeleteBehavior.Restrict);
                b.Property(x => x.CorrectionReason).HasMaxLength(2000);
                b.HasOne<AssessmentReportRevision>()
                    .WithMany()
                    .HasForeignKey(x => new { x.AssessmentReportId, x.BasedOnRevisionId })
                    .HasPrincipalKey(x => new { x.AssessmentReportId, x.Id })
                    .HasConstraintName("FK_AssessmentReportRevisions_BasedOn_SameReport")
                    .OnDelete(DeleteBehavior.Restrict);
                b.HasIndex(x => new { x.AssessmentReportId, x.BasedOnRevisionId })
                    .HasDatabaseName("IX_AssessmentReportRevisions_Report_BasedOn");
                b.HasIndex(new[] { nameof(AssessmentReportRevision.AssessmentReportId) }, "UX_AssessmentReportRevisions_Report_ActiveRevision")
                    .IsUnique()
                    .HasFilter("\"Status\" IN (0, 1, 2)");
                b.HasIndex(new[] { nameof(AssessmentReportRevision.AssessmentReportId) }, "UX_AssessmentReportRevisions_Report_CurrentIssued")
                    .IsUnique()
                    .HasFilter("\"Status\" = 3");
                b.HasIndex(x => new { x.AssessmentReportId, x.SupersededByRevisionId })
                    .HasDatabaseName("IX_AssessmentReportRevisions_Report_SupersededBy");
                b.HasIndex(x => x.SupersededByRevisionId)
                    .IsUnique()
                    .HasFilter("\"SupersededByRevisionId\" IS NOT NULL")
                    .HasDatabaseName("UX_AssessmentReportRevisions_SupersededBy");

                b.ToTable(table =>
                {
                    table.HasCheckConstraint("CK_AssessmentReportRevision_RevisionNumber_Positive", "\"RevisionNumber\" > 0");
                    table.HasCheckConstraint("CK_AssessmentReportRevision_StatusValues",
                        "\"Status\" BETWEEN 0 AND 5");
                    table.HasCheckConstraint("CK_AssessmentReportRevision_NoSelfSupersession",
                        "\"SupersededByRevisionId\" IS NULL OR \"SupersededByRevisionId\" <> \"Id\"");
                    table.HasCheckConstraint("CK_AssessmentReportRevision_NoSelfLineage",
                        "\"BasedOnRevisionId\" IS NULL OR \"BasedOnRevisionId\" <> \"Id\"");
                    table.HasCheckConstraint("CK_AssessmentReportRevision_CorrectionReason",
                        "\"CorrectionReason\" IS NULL OR (\"BasedOnRevisionId\" IS NOT NULL AND length(trim(\"CorrectionReason\")) > 0 AND length(\"CorrectionReason\") <= 2000)");
                    table.HasCheckConstraint("CK_AssessmentReportRevision_SupersededLink",
                        "(\"Status\" = 4) = (\"SupersededByRevisionId\" IS NOT NULL)");
                    table.HasCheckConstraint("CK_AssessmentReportRevision_StampPairs",
                        "((\"SubmittedForReviewUtc\" IS NULL) = (\"SubmittedForReviewByUserId\" IS NULL)) AND " +
                        "((\"ApprovedUtc\" IS NULL) = (\"ApprovedByUserId\" IS NULL)) AND " +
                        "((\"IssuedUtc\" IS NULL) = (\"IssuedByUserId\" IS NULL)) AND " +
                        "(\"SubmittedForReviewByUserId\" IS NULL OR length(trim(\"SubmittedForReviewByUserId\")) > 0) AND " +
                        "(\"ApprovedByUserId\" IS NULL OR length(trim(\"ApprovedByUserId\")) > 0) AND " +
                        "(\"IssuedByUserId\" IS NULL OR length(trim(\"IssuedByUserId\")) > 0)");
                    table.HasCheckConstraint("CK_AssessmentReportRevision_StatusStamps",
                        "(\"Status\" = 0 AND \"SubmittedForReviewUtc\" IS NULL AND \"ApprovedUtc\" IS NULL AND \"IssuedUtc\" IS NULL) OR " +
                        "(\"Status\" = 1 AND \"SubmittedForReviewUtc\" IS NOT NULL AND \"ApprovedUtc\" IS NULL AND \"IssuedUtc\" IS NULL) OR " +
                        "(\"Status\" = 2 AND \"SubmittedForReviewUtc\" IS NOT NULL AND \"ApprovedUtc\" IS NOT NULL AND \"IssuedUtc\" IS NULL) OR " +
                        "(\"Status\" IN (3, 4) AND \"SubmittedForReviewUtc\" IS NOT NULL AND \"ApprovedUtc\" IS NOT NULL AND \"IssuedUtc\" IS NOT NULL) OR " +
                        "(\"Status\" = 5 AND \"SubmittedForReviewUtc\" IS NOT NULL AND \"IssuedUtc\" IS NULL)");
                });
            });

            modelBuilder.Entity<AssessmentReportTransitionHistory>(b =>
            {
                b.HasKey(x => x.Id);

                b.Property(x => x.EventType).HasColumnType("smallint").IsRequired();
                b.Property(x => x.PreviousStatus).HasColumnType("smallint");
                b.Property(x => x.NewStatus).HasColumnType("smallint").IsRequired();
                b.Property(x => x.ActorUserId).IsRequired().HasMaxLength(450);
                b.Property(x => x.OccurredUtc).HasColumnType("timestamp with time zone").IsRequired();
                b.Property(x => x.Reason).HasMaxLength(2000);
                b.Property(x => x.CorrelationId).IsRequired().HasMaxLength(128);

                b.HasOne<AssessmentReportRevision>()
                    .WithMany()
                    .HasForeignKey(x => new { x.AssessmentReportId, x.AssessmentReportRevisionId })
                    .HasPrincipalKey(x => new { x.AssessmentReportId, x.Id })
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne<AssessmentReportRevision>()
                    .WithMany()
                    .HasForeignKey(x => new { x.AssessmentReportId, x.ReplacedRevisionId })
                    .HasPrincipalKey(x => new { x.AssessmentReportId, x.Id })
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasOne<AssessmentReportRevision>()
                    .WithMany()
                    .HasForeignKey(x => new { x.AssessmentReportId, x.ReplacementRevisionId })
                    .HasPrincipalKey(x => new { x.AssessmentReportId, x.Id })
                    .OnDelete(DeleteBehavior.Restrict);

                b.HasIndex(x => new { x.AssessmentReportRevisionId, x.OccurredUtc, x.Id });
                b.HasIndex(x => new { x.AssessmentReportId, x.OccurredUtc });
                b.HasIndex(x => x.AssessmentReportRevisionId)
                    .IsUnique()
                    .HasFilter("\"EventType\" = 1")
                    .HasDatabaseName("UX_AssessmentReportTransitionHistory_Revision_Created");
                b.HasIndex(x => new { x.AssessmentReportRevisionId, x.NewStatus })
                    .IsUnique()
                    .HasFilter("\"EventType\" = 2")
                    .HasDatabaseName("UX_AssessmentReportTransitionHistory_Revision_NewStatus");

                b.ToTable(table =>
                {
                    table.HasCheckConstraint("CK_AssessmentReportTransitionHistory_EventType",
                        "\"EventType\" IN (1, 2)");
                    table.HasCheckConstraint("CK_AssessmentReportTransitionHistory_StatusValues",
                        "\"NewStatus\" BETWEEN 0 AND 5 AND (\"PreviousStatus\" IS NULL OR \"PreviousStatus\" BETWEEN 0 AND 5)");
                    table.HasCheckConstraint("CK_AssessmentReportTransitionHistory_EventShape",
                        "(\"EventType\" = 1 AND \"PreviousStatus\" IS NULL AND \"NewStatus\" = 0) OR (\"EventType\" = 2 AND \"PreviousStatus\" IS NOT NULL AND (" +
                        "(\"PreviousStatus\" = 0 AND \"NewStatus\" = 1) OR (\"PreviousStatus\" = 1 AND \"NewStatus\" = 2) OR " +
                        "(\"PreviousStatus\" = 1 AND \"NewStatus\" = 5) OR (\"PreviousStatus\" = 2 AND \"NewStatus\" = 5) OR " +
                        "(\"PreviousStatus\" = 2 AND \"NewStatus\" = 3) OR (\"PreviousStatus\" = 3 AND \"NewStatus\" = 4)))");
                    table.HasCheckConstraint("CK_AssessmentReportTransitionHistory_Reason",
                        "(\"NewStatus\" = 5 AND \"Reason\" IS NOT NULL AND \"Reason\" = trim(\"Reason\") AND length(\"Reason\") BETWEEN 1 AND 2000) OR (\"NewStatus\" <> 5 AND \"Reason\" IS NULL)");
                    table.HasCheckConstraint("CK_AssessmentReportTransitionHistory_ReplacementRevision",
                        "CASE WHEN \"EventType\" = 2 AND \"PreviousStatus\" = 3 AND \"NewStatus\" = 4 " +
                        "THEN \"ReplacementRevisionId\" IS NOT NULL AND \"ReplacementRevisionId\" <> \"AssessmentReportRevisionId\" " +
                        "ELSE \"ReplacementRevisionId\" IS NULL END");
                    table.HasCheckConstraint("CK_AssessmentReportTransitionHistory_ReplacedRevision",
                        "CASE WHEN \"EventType\" = 2 AND \"PreviousStatus\" = 2 AND \"NewStatus\" = 3 " +
                        "THEN \"ReplacedRevisionId\" IS NULL OR \"ReplacedRevisionId\" <> \"AssessmentReportRevisionId\" " +
                        "ELSE \"ReplacedRevisionId\" IS NULL END");
                    table.HasCheckConstraint("CK_AssessmentReportTransitionHistory_Identifiers",
                        "length(trim(\"ActorUserId\")) > 0 AND length(trim(\"CorrelationId\")) > 0");
                });
            });

        }
    }
}
