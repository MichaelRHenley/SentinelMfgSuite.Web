using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentInvitationPostgresIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task IssueWithinExistingTransactionAsync_OuterCommit_Persists_Assessment_And_Invitation()
    {
        var fixture = CreateFixtureOrSkip();
        await fixture.InitializeAsync();

        var records = new DisposableTestRecords();
        try
        {
            var assessmentId = Guid.NewGuid();
            var nowUtc = DateTime.UtcNow;

            await using (var actionDb = fixture.CreateDbContext())
            {
                await using var tx = await actionDb.Database.BeginTransactionAsync();

                actionDb.Set<Assessment>().Add(new Assessment
                {
                    Id = assessmentId,
                    Title = $"pg-int-assessment-{assessmentId:N}",
                    SiteName = "Integration Site",
                    ContactEmail = $"integration+{assessmentId:N}@example.com",
                    Status = AssessmentStatus.Draft,
                    CreatedUtc = nowUtc,
                    UpdatedUtc = nowUtc
                });
                await actionDb.SaveChangesAsync();

                var service = new AssessmentInvitationIssuanceService(actionDb);
                var participant = (IAssessmentInvitationIssuanceTransactionParticipant)service;
                var result = await participant.IssueWithinExistingTransactionAsync(assessmentId, TimeSpan.FromHours(3));

                Assert.True(result.Succeeded);
                records.AssessmentIds.Add(assessmentId);
                records.InvitationIds.Add(result.InvitationId!.Value);

                await tx.CommitAsync();
            }

            await using var verifyDb = fixture.CreateDbContext();
            var savedAssessment = await verifyDb.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == assessmentId);
            var savedInvitation = await verifyDb.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.AssessmentId == assessmentId && x.RevokedUtc == null);

            Assert.Equal(AssessmentStatus.Invited, savedAssessment.Status);
            Assert.Equal(assessmentId, savedInvitation.AssessmentId);
        }
        finally
        {
            await CleanupAsync(fixture, records);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task IssueWithinExistingTransactionAsync_OuterRollback_Does_Not_Persist_Assessment_Or_Invitation()
    {
        var fixture = CreateFixtureOrSkip();
        await fixture.InitializeAsync();

        var assessmentId = Guid.NewGuid();
        var nowUtc = DateTime.UtcNow;

        await using (var actionDb = fixture.CreateDbContext())
        {
            await using var tx = await actionDb.Database.BeginTransactionAsync();

            actionDb.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = $"pg-int-assessment-{assessmentId:N}",
                SiteName = "Integration Site",
                ContactEmail = $"integration+{assessmentId:N}@example.com",
                Status = AssessmentStatus.Draft,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc
            });
            await actionDb.SaveChangesAsync();

            var service = new AssessmentInvitationIssuanceService(actionDb);
            var participant = (IAssessmentInvitationIssuanceTransactionParticipant)service;
            var result = await participant.IssueWithinExistingTransactionAsync(assessmentId, TimeSpan.FromHours(3));

            Assert.True(result.Succeeded);
            await tx.RollbackAsync();
        }

        await using var verifyDb = fixture.CreateDbContext();
        var assessmentExists = await verifyDb.Set<Assessment>().AsNoTracking().AnyAsync(x => x.Id == assessmentId);
        var invitationExists = await verifyDb.Set<AssessmentInvitation>().AsNoTracking().AnyAsync(x => x.AssessmentId == assessmentId);

        Assert.False(assessmentExists);
        Assert.False(invitationExists);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ReplaceAsync_Replaces_Pending_Invitation_And_Revokes_Previous()
    {
        var fixture = CreateFixtureOrSkip();
        await fixture.InitializeAsync();

        var records = new DisposableTestRecords();
        try
        {
            var assessmentId = Guid.NewGuid();
            var existingInvitationId = Guid.NewGuid();
            var nowUtc = DateTime.UtcNow;

            await using (var seedDb = fixture.CreateDbContext())
            {
                seedDb.Set<Assessment>().Add(new Assessment
                {
                    Id = assessmentId,
                    Title = $"pg-int-assessment-{assessmentId:N}",
                    SiteName = "Integration Site",
                    ContactEmail = $"integration+{assessmentId:N}@example.com",
                    Status = AssessmentStatus.Invited,
                    CreatedUtc = nowUtc,
                    UpdatedUtc = nowUtc
                });

                seedDb.Set<AssessmentInvitation>().Add(new AssessmentInvitation
                {
                    Id = existingInvitationId,
                    AssessmentId = assessmentId,
                    Email = $"integration+{assessmentId:N}@example.com",
                    TokenHash = Guid.NewGuid().ToString("N"),
                    CreatedUtc = nowUtc,
                    ExpiresUtc = nowUtc.AddHours(2),
                    DeliveryStatus = AssessmentInvitationDeliveryStatus.Pending,
                    DeliveryAttemptCount = 0,
                    AccessCount = 0,
                    LastAccessUtc = null
                });

                await seedDb.SaveChangesAsync();
            }

            records.AssessmentIds.Add(assessmentId);
            records.InvitationIds.Add(existingInvitationId);

            AssessmentInvitationReplacementIssuanceResult result;
            await using (var actionDb = fixture.CreateDbContext())
            {
                var service = new AssessmentInvitationIssuanceService(actionDb);
                result = await service.ReplaceAsync(assessmentId, existingInvitationId, TimeSpan.FromHours(8));
            }

            Assert.True(result.Succeeded);
            Assert.NotNull(result.InvitationId);
            Assert.False(string.IsNullOrWhiteSpace(result.InvitationToken));
            records.InvitationIds.Add(result.InvitationId!.Value);

            await using var verifyDb = fixture.CreateDbContext();
            var oldInvitation = await verifyDb.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == existingInvitationId);
            var replacement = await verifyDb.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == result.InvitationId.Value);
            var assessment = await verifyDb.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == assessmentId);

            Assert.NotNull(oldInvitation.RevokedUtc);
            Assert.Null(replacement.RevokedUtc);
            Assert.Equal(AssessmentInvitationDeliveryStatus.Pending, replacement.DeliveryStatus);
            Assert.Equal(AssessmentStatus.Invited, assessment.Status);
            Assert.NotEqual(oldInvitation.Id, replacement.Id);
        }
        finally
        {
            await CleanupAsync(fixture, records);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ReplaceAsync_Replaces_Failed_Invitation_And_Leaves_Assessment_Invited()
    {
        var fixture = CreateFixtureOrSkip();
        await fixture.InitializeAsync();

        var records = new DisposableTestRecords();
        try
        {
            var assessmentId = Guid.NewGuid();
            var existingInvitationId = Guid.NewGuid();
            var nowUtc = DateTime.UtcNow;

            await using (var seedDb = fixture.CreateDbContext())
            {
                seedDb.Set<Assessment>().Add(new Assessment
                {
                    Id = assessmentId,
                    Title = $"pg-int-assessment-{assessmentId:N}",
                    SiteName = "Integration Site",
                    ContactEmail = $"integration+{assessmentId:N}@example.com",
                    Status = AssessmentStatus.Invited,
                    CreatedUtc = nowUtc,
                    UpdatedUtc = nowUtc
                });

                seedDb.Set<AssessmentInvitation>().Add(new AssessmentInvitation
                {
                    Id = existingInvitationId,
                    AssessmentId = assessmentId,
                    Email = $"integration+{assessmentId:N}@example.com",
                    TokenHash = Guid.NewGuid().ToString("N"),
                    CreatedUtc = nowUtc,
                    ExpiresUtc = nowUtc.AddHours(2),
                    DeliveryStatus = AssessmentInvitationDeliveryStatus.Failed,
                    DeliveryFailureCode = "EmailProviderFailure",
                    DeliveryAttemptCount = 1,
                    AccessCount = 0,
                    LastAccessUtc = null
                });

                await seedDb.SaveChangesAsync();
            }

            records.AssessmentIds.Add(assessmentId);
            records.InvitationIds.Add(existingInvitationId);

            AssessmentInvitationReplacementIssuanceResult result;
            await using (var actionDb = fixture.CreateDbContext())
            {
                var service = new AssessmentInvitationIssuanceService(actionDb);
                result = await service.ReplaceAsync(assessmentId, existingInvitationId, TimeSpan.FromHours(4));
            }

            Assert.True(result.Succeeded);
            Assert.NotNull(result.InvitationId);
            records.InvitationIds.Add(result.InvitationId!.Value);

            await using var verifyDb = fixture.CreateDbContext();
            var oldInvitation = await verifyDb.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == existingInvitationId);
            var replacement = await verifyDb.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == result.InvitationId.Value);
            var assessment = await verifyDb.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == assessmentId);

            Assert.NotNull(oldInvitation.RevokedUtc);
            Assert.Null(replacement.RevokedUtc);
            Assert.Equal(AssessmentInvitationDeliveryStatus.Pending, replacement.DeliveryStatus);
            Assert.Equal(AssessmentStatus.Invited, assessment.Status);
        }
        finally
        {
            await CleanupAsync(fixture, records);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ReplaceAsync_Concurrent_Attempts_Exactly_One_Succeeds()
    {
        var fixture = CreateFixtureOrSkip();
        await fixture.InitializeAsync();

        var records = new DisposableTestRecords();
        try
        {
            var assessmentId = Guid.NewGuid();
            var existingInvitationId = Guid.NewGuid();
            var nowUtc = DateTime.UtcNow;

            await using (var seedDb = fixture.CreateDbContext())
            {
                seedDb.Set<Assessment>().Add(new Assessment
                {
                    Id = assessmentId,
                    Title = $"pg-int-assessment-{assessmentId:N}",
                    SiteName = "Integration Site",
                    ContactEmail = $"integration+{assessmentId:N}@example.com",
                    Status = AssessmentStatus.Invited,
                    CreatedUtc = nowUtc,
                    UpdatedUtc = nowUtc
                });

                seedDb.Set<AssessmentInvitation>().Add(new AssessmentInvitation
                {
                    Id = existingInvitationId,
                    AssessmentId = assessmentId,
                    Email = $"integration+{assessmentId:N}@example.com",
                    TokenHash = Guid.NewGuid().ToString("N"),
                    CreatedUtc = nowUtc,
                    ExpiresUtc = nowUtc.AddHours(3),
                    DeliveryStatus = AssessmentInvitationDeliveryStatus.Pending,
                    DeliveryAttemptCount = 0,
                    AccessCount = 0,
                    LastAccessUtc = null
                });

                await seedDb.SaveChangesAsync();
            }

            records.AssessmentIds.Add(assessmentId);
            records.InvitationIds.Add(existingInvitationId);

            async Task<AssessmentInvitationReplacementIssuanceResult> AttemptReplaceAsync()
            {
                await using var db = fixture.CreateDbContext();
                var service = new AssessmentInvitationIssuanceService(db);
                return await service.ReplaceAsync(assessmentId, existingInvitationId, TimeSpan.FromHours(5));
            }

            var t1 = AttemptReplaceAsync();
            var t2 = AttemptReplaceAsync();
            var results = await Task.WhenAll(t1, t2);

            var successCount = results.Count(r => r.Succeeded);
            Assert.Equal(1, successCount);

            var winning = results.Single(r => r.Succeeded);
            records.InvitationIds.Add(winning.InvitationId!.Value);

            await using var verifyDb = fixture.CreateDbContext();
            var currentInvitations = await verifyDb.Set<AssessmentInvitation>()
                .AsNoTracking()
                .Where(x => x.AssessmentId == assessmentId)
                .ToListAsync();

            Assert.Equal(2, currentInvitations.Count);
            Assert.Equal(1, currentInvitations.Count(x => x.RevokedUtc is null));
            Assert.Equal(1, currentInvitations.Count(x => x.RevokedUtc is not null));
        }
        finally
        {
            await CleanupAsync(fixture, records);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ReplaceAsync_Insert_Failure_Rolls_Back_Revocation()
    {
        var fixture = CreateFixtureOrSkip();
        await fixture.InitializeAsync();

        var records = new DisposableTestRecords();
        try
        {
            var assessmentId = Guid.NewGuid();
            var existingInvitationId = Guid.NewGuid();
            var forcedToken = "pg-forced-collision-token";
            var forcedTokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(forcedToken))).ToLowerInvariant();
            var nowUtc = DateTime.UtcNow;

            var otherAssessmentId = Guid.NewGuid();
            var collidingInvitationId = Guid.NewGuid();

            await using (var seedDb = fixture.CreateDbContext())
            {
                seedDb.Set<Assessment>().AddRange(
                    new Assessment
                    {
                        Id = assessmentId,
                        Title = $"pg-int-assessment-{assessmentId:N}",
                        SiteName = "Integration Site",
                        ContactEmail = $"integration+{assessmentId:N}@example.com",
                        Status = AssessmentStatus.Invited,
                        CreatedUtc = nowUtc,
                        UpdatedUtc = nowUtc
                    },
                    new Assessment
                    {
                        Id = otherAssessmentId,
                        Title = $"pg-int-assessment-{otherAssessmentId:N}",
                        SiteName = "Integration Site",
                        ContactEmail = $"integration+{otherAssessmentId:N}@example.com",
                        Status = AssessmentStatus.Invited,
                        CreatedUtc = nowUtc,
                        UpdatedUtc = nowUtc
                    });

                seedDb.Set<AssessmentInvitation>().AddRange(
                    new AssessmentInvitation
                    {
                        Id = existingInvitationId,
                        AssessmentId = assessmentId,
                        Email = $"integration+{assessmentId:N}@example.com",
                        TokenHash = Guid.NewGuid().ToString("N"),
                        CreatedUtc = nowUtc,
                        ExpiresUtc = nowUtc.AddHours(2),
                        DeliveryStatus = AssessmentInvitationDeliveryStatus.Pending,
                        DeliveryAttemptCount = 0,
                        AccessCount = 0,
                        LastAccessUtc = null
                    },
                    new AssessmentInvitation
                    {
                        Id = collidingInvitationId,
                        AssessmentId = otherAssessmentId,
                        Email = $"integration+{otherAssessmentId:N}@example.com",
                        TokenHash = forcedTokenHash,
                        CreatedUtc = nowUtc,
                        ExpiresUtc = nowUtc.AddHours(2),
                        DeliveryStatus = AssessmentInvitationDeliveryStatus.Pending,
                        DeliveryAttemptCount = 0,
                        AccessCount = 0,
                        LastAccessUtc = null
                    });

                await seedDb.SaveChangesAsync();
            }

            records.AssessmentIds.Add(assessmentId);
            records.AssessmentIds.Add(otherAssessmentId);
            records.InvitationIds.Add(existingInvitationId);
            records.InvitationIds.Add(collidingInvitationId);

            AssessmentInvitationReplacementIssuanceResult result;
            await using (var actionDb = fixture.CreateDbContext())
            {
                var service = new AssessmentInvitationIssuanceService(actionDb, () => forcedToken);
                result = await service.ReplaceAsync(assessmentId, existingInvitationId, TimeSpan.FromHours(6));
            }

            Assert.False(result.Succeeded);
            Assert.Equal("ReplacementFailed", result.FailureCode);

            await using var verifyDb = fixture.CreateDbContext();
            var original = await verifyDb.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == existingInvitationId);
            Assert.Null(original.RevokedUtc);
        }
        finally
        {
            await CleanupAsync(fixture, records);
        }
    }

    private static PostgresIntegrationTestFixture CreateFixtureOrSkip()
    {
        var connection = Environment.GetEnvironmentVariable(PostgresIntegrationTestFixture.ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException($"Set {PostgresIntegrationTestFixture.ConnectionEnvironmentVariable} to run PostgreSQL integration tests.");
        }

        return new PostgresIntegrationTestFixture();
    }

    private static async Task CleanupAsync(PostgresIntegrationTestFixture fixture, DisposableTestRecords records)
    {
        await using var db = fixture.CreateDbContext();

        if (records.ResponseIds.Count > 0)
        {
            await db.Set<AssessmentResponse>()
                .Where(x => records.ResponseIds.Contains(x.Id))
                .ExecuteDeleteAsync();
        }

        if (records.InvitationIds.Count > 0)
        {
            await db.Set<AssessmentInvitation>()
                .Where(x => records.InvitationIds.Contains(x.Id))
                .ExecuteDeleteAsync();
        }

        if (records.AssessmentIds.Count > 0)
        {
            await db.Set<Assessment>()
                .Where(x => records.AssessmentIds.Contains(x.Id))
                .ExecuteDeleteAsync();
        }
    }

    private sealed class DisposableTestRecords
    {
        public List<Guid> AssessmentIds { get; } = new();
        public List<Guid> InvitationIds { get; } = new();
        public List<Guid> ResponseIds { get; } = new();
    }
}
