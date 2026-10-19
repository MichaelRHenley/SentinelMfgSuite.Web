using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentInvitationIssuanceServiceTests
{
    [Fact]
    public void Program_Registers_AssessmentInvitationIssuanceService_As_Scoped()
    {
        var programPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Program.cs"));
        Assert.True(File.Exists(programPath), $"Program.cs not found at '{programPath}'.");

        var programContent = File.ReadAllText(programPath);
        Assert.Matches(
            new Regex(@"AddScoped\s*<\s*IAssessmentInvitationIssuanceService\s*,\s*AssessmentInvitationIssuanceService\s*>\s*\(\s*\)\s*;", RegexOptions.CultureInvariant),
            programContent);
    }

    [Fact]
    public async Task IssueAsync_For_Draft_Assessment_Succeeds_And_Persists_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Draft, "customer@example.com");
        var service = new AssessmentInvitationIssuanceService(fixture.Db);

        var result = await service.IssueAsync(assessment.Id, TimeSpan.FromHours(24));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.InvitationId);
        Assert.False(string.IsNullOrWhiteSpace(result.InvitationToken));
        Assert.NotNull(result.ExpiresUtc);

        var savedAssessment = await fixture.Db.Set<Assessment>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == assessment.Id);
        Assert.Equal(AssessmentStatus.Invited, savedAssessment.Status);

        var invitation = await fixture.Db.Set<AssessmentInvitation>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == result.InvitationId!.Value);

        Assert.Equal(assessment.Id, invitation.AssessmentId);
        Assert.Equal("customer@example.com", invitation.Email);
        Assert.NotEqual(default, invitation.CreatedUtc);
        Assert.True(invitation.ExpiresUtc > invitation.CreatedUtc);
        Assert.Equal(0, invitation.AccessCount);
    }

    [Fact]
    public async Task IssueAsync_TokenHash_Is_Compatible_With_AssessmentInvitationAccessService()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Draft, "customer@example.com");
        var issuanceService = new AssessmentInvitationIssuanceService(fixture.Db);

        var issued = await issuanceService.IssueAsync(assessment.Id, TimeSpan.FromHours(1));

        Assert.True(issued.Succeeded);
        var accessService = new AssessmentInvitationAccessService(fixture.Db);
        var access = await accessService.ValidateAccessAsync(issued.InvitationId!.Value, issued.InvitationToken);

        Assert.True(access.IsAuthorized);
        Assert.Equal(AssessmentInvitationAccessState.Authorized, access.AccessState);
    }

    [Fact]
    public async Task IssueAsync_Uses_Expiration_Period_And_Invitation_Expires_For_Access()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Draft, "customer@example.com");
        var issuanceService = new AssessmentInvitationIssuanceService(fixture.Db);

        var issued = await issuanceService.IssueAsync(assessment.Id, TimeSpan.FromMilliseconds(25));

        Assert.True(issued.Succeeded);
        Assert.NotNull(issued.ExpiresUtc);

        await Task.Delay(80);

        var accessService = new AssessmentInvitationAccessService(fixture.Db);
        var access = await accessService.ValidateAccessAsync(issued.InvitationId!.Value, issued.InvitationToken);

        Assert.Equal(AssessmentInvitationAccessState.Expired, access.AccessState);
        Assert.False(access.IsAuthorized);
    }

    [Theory]
    [InlineData(AssessmentStatus.InProgress)]
    [InlineData(AssessmentStatus.Submitted)]
    [InlineData(AssessmentStatus.Completed)]
    [InlineData(AssessmentStatus.Cancelled)]
    public async Task IssueAsync_Rejects_Ineligible_Assessment_Status(AssessmentStatus status)
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(status, "customer@example.com");
        var service = new AssessmentInvitationIssuanceService(fixture.Db);

        var result = await service.IssueAsync(assessment.Id, TimeSpan.FromHours(1));

        Assert.False(result.Succeeded);
        Assert.Equal("IneligibleAssessmentStatus", result.FailureCode);

        var invitationCount = await fixture.Db.Set<AssessmentInvitation>().CountAsync(x => x.AssessmentId == assessment.Id);
        Assert.Equal(0, invitationCount);
    }

    [Fact]
    public async Task IssueAsync_Rejects_Missing_Assessment()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var service = new AssessmentInvitationIssuanceService(fixture.Db);

        var result = await service.IssueAsync(Guid.NewGuid(), TimeSpan.FromHours(1));

        Assert.False(result.Succeeded);
        Assert.Equal("AssessmentNotFound", result.FailureCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task IssueAsync_Rejects_Invalid_Expiration_Period(int milliseconds)
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Draft, "customer@example.com");
        var service = new AssessmentInvitationIssuanceService(fixture.Db);

        var result = await service.IssueAsync(assessment.Id, TimeSpan.FromMilliseconds(milliseconds));

        Assert.False(result.Succeeded);
        Assert.Equal("InvalidExpirationPeriod", result.FailureCode);

        var savedAssessment = await fixture.Db.Set<Assessment>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == assessment.Id);
        Assert.Equal(AssessmentStatus.Draft, savedAssessment.Status);

        var invitationCount = await fixture.Db.Set<AssessmentInvitation>().CountAsync(x => x.AssessmentId == assessment.Id);
        Assert.Equal(0, invitationCount);
    }

    [Fact]
    public async Task IssueAsync_Prevents_Duplicate_Active_Invitations()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Draft, "customer@example.com");
        var service = new AssessmentInvitationIssuanceService(fixture.Db);

        var first = await service.IssueAsync(assessment.Id, TimeSpan.FromHours(1));
        var second = await service.IssueAsync(assessment.Id, TimeSpan.FromHours(1));

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal("DuplicateActiveInvitation", second.FailureCode);

        var invitationCount = await fixture.Db.Set<AssessmentInvitation>().CountAsync(x => x.AssessmentId == assessment.Id);
        Assert.Equal(1, invitationCount);
    }

    [Fact]
    public async Task IssueAsync_Does_Not_Persist_Raw_Token()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Draft, "customer@example.com");
        var service = new AssessmentInvitationIssuanceService(fixture.Db);

        var result = await service.IssueAsync(assessment.Id, TimeSpan.FromHours(1));

        Assert.True(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.InvitationToken));

        var invitation = await fixture.Db.Set<AssessmentInvitation>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == result.InvitationId!.Value);

        Assert.NotEqual(result.InvitationToken, invitation.TokenHash);
        Assert.DoesNotContain(result.InvitationToken!, invitation.TokenHash, StringComparison.Ordinal);

        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(result.InvitationToken!))).ToLowerInvariant();
        Assert.Equal(expectedHash, invitation.TokenHash);
    }

    [Fact]
    public async Task IssueWithinExistingTransactionAsync_Requires_Active_Transaction()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Draft, "customer@example.com");
        var service = new AssessmentInvitationIssuanceService(fixture.Db);
        var participant = (IAssessmentInvitationIssuanceTransactionParticipant)service;

        var result = await participant.IssueWithinExistingTransactionAsync(assessment.Id, TimeSpan.FromHours(1));

        Assert.False(result.Succeeded);
        Assert.Equal("MissingTransaction", result.FailureCode);
        Assert.Null(fixture.Db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task IssueWithinExistingTransactionAsync_OuterCommit_Persists_Assessment_And_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Draft, "customer@example.com");
        var service = new AssessmentInvitationIssuanceService(fixture.Db);
        var participant = (IAssessmentInvitationIssuanceTransactionParticipant)service;

        await using var tx = await fixture.Db.Database.BeginTransactionAsync();

        var result = await participant.IssueWithinExistingTransactionAsync(assessment.Id, TimeSpan.FromHours(1));
        Assert.True(result.Succeeded);

        await tx.CommitAsync();

        var savedAssessment = await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == assessment.Id);
        var savedInvitation = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == result.InvitationId!.Value);

        Assert.Equal(AssessmentStatus.Invited, savedAssessment.Status);
        Assert.Equal("1.0", savedAssessment.QuestionnaireVersion);
        Assert.Equal(assessment.Id, savedInvitation.AssessmentId);
    }

    [Fact]
    public async Task IssueWithinExistingTransactionAsync_OuterRollback_Removes_Assessment_And_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var service = new AssessmentInvitationIssuanceService(fixture.Db);
        var participant = (IAssessmentInvitationIssuanceTransactionParticipant)service;

        var transientAssessmentId = Guid.NewGuid();

        await using (var tx = await fixture.Db.Database.BeginTransactionAsync())
        {
            fixture.Db.Set<Assessment>().Add(new Assessment
            {
                Id = transientAssessmentId,
                Title = "Transient",
                SiteName = "Transient",
                ContactEmail = "customer@example.com",
                Status = AssessmentStatus.Draft,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            await fixture.Db.SaveChangesAsync();

            var result = await participant.IssueWithinExistingTransactionAsync(transientAssessmentId, TimeSpan.FromHours(1));
            Assert.True(result.Succeeded);

            await tx.RollbackAsync();
        }

        var assessmentExists = await fixture.Db.Set<Assessment>().AsNoTracking().AnyAsync(x => x.Id == transientAssessmentId);
        Assert.False(assessmentExists);
    }

    [Fact]
    public async Task ReplaceAsync_For_Pending_Invitation_Succeeds_And_Revokes_Old_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(
            assessment.Id,
            "customer@example.com",
            AssessmentInvitationDeliveryStatus.Pending,
            DateTime.UtcNow.AddHours(2));

        var service = new AssessmentInvitationIssuanceService(fixture.Db);

        var result = await service.ReplaceAsync(assessment.Id, existing.Id, TimeSpan.FromHours(6));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.InvitationId);
        Assert.NotEqual(existing.Id, result.InvitationId!.Value);
        Assert.False(string.IsNullOrWhiteSpace(result.InvitationToken));

        var oldInvitation = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.NotNull(oldInvitation.RevokedUtc);

        var replacement = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == result.InvitationId.Value);
        Assert.Equal(assessment.Id, replacement.AssessmentId);
        Assert.Equal("customer@example.com", replacement.Email);
        Assert.Null(replacement.RevokedUtc);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Pending, replacement.DeliveryStatus);
        Assert.Equal(0, replacement.DeliveryAttemptCount);
        Assert.Equal("1.0", (await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == assessment.Id)).QuestionnaireVersion);

        var accessService = new AssessmentInvitationAccessService(fixture.Db);
        var access = await accessService.ValidateAccessAsync(replacement.Id, result.InvitationToken);
        Assert.True(access.IsAuthorized);
    }

    [Fact]
    public async Task ReplaceAsync_For_Failed_Invitation_Succeeds_And_Preserves_Assessment_Status()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(
            assessment.Id,
            "customer@example.com",
            AssessmentInvitationDeliveryStatus.Failed,
            DateTime.UtcNow.AddHours(2));

        var service = new AssessmentInvitationIssuanceService(fixture.Db);

        var result = await service.ReplaceAsync(assessment.Id, existing.Id, TimeSpan.FromHours(4));

        Assert.True(result.Succeeded);

        var savedAssessment = await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == assessment.Id);
        Assert.Equal(AssessmentStatus.Invited, savedAssessment.Status);
        Assert.Equal("1.0", savedAssessment.QuestionnaireVersion);
    }

    [Theory]
    [InlineData(AssessmentInvitationDeliveryStatus.Sending)]
    [InlineData(AssessmentInvitationDeliveryStatus.OutcomeUnknown)]
    [InlineData(AssessmentInvitationDeliveryStatus.Accepted)]
    public async Task ReplaceAsync_Rejects_Ineligible_Delivery_Statuses(AssessmentInvitationDeliveryStatus deliveryStatus)
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", deliveryStatus, DateTime.UtcNow.AddHours(2));
        var service = new AssessmentInvitationIssuanceService(fixture.Db);

        var result = await service.ReplaceAsync(assessment.Id, existing.Id, TimeSpan.FromHours(3));

        Assert.False(result.Succeeded);
        Assert.Equal("IneligibleDeliveryStatus", result.FailureCode);
    }

    [Fact]
    public async Task ReplaceAsync_Rejects_When_Invitation_Has_Access_Activity()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(
            assessment.Id,
            "customer@example.com",
            AssessmentInvitationDeliveryStatus.Pending,
            DateTime.UtcNow.AddHours(2),
            accessCount: 1,
            lastAccessUtc: DateTime.UtcNow.AddMinutes(-5));
        var service = new AssessmentInvitationIssuanceService(fixture.Db);

        var result = await service.ReplaceAsync(assessment.Id, existing.Id, TimeSpan.FromHours(3));

        Assert.False(result.Succeeded);
        Assert.Equal("InvitationAlreadyUsed", result.FailureCode);
    }

    [Fact]
    public async Task ReplaceAsync_Rejects_When_Response_Exists()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2));

        fixture.Db.Set<AssessmentResponse>().Add(new AssessmentResponse
        {
            Id = Guid.NewGuid(),
            AssessmentId = assessment.Id,
            InvitationId = existing.Id,
            Data = "{}",
            IsDraft = true,
            LastSavedUtc = DateTime.UtcNow,
            SubmittedUtc = null,
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        });
        await fixture.Db.SaveChangesAsync();

        var service = new AssessmentInvitationIssuanceService(fixture.Db);
        var result = await service.ReplaceAsync(assessment.Id, existing.Id, TimeSpan.FromHours(3));

        Assert.False(result.Succeeded);
        Assert.Equal("InvitationAlreadyUsed", result.FailureCode);
    }

    [Fact]
    public async Task ReplaceAsync_Rejects_When_Another_Active_Invitation_Exists()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2));
        await fixture.SeedInvitationAsync(assessment.Id, "other@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2));

        var service = new AssessmentInvitationIssuanceService(fixture.Db);
        var result = await service.ReplaceAsync(assessment.Id, existing.Id, TimeSpan.FromHours(3));

        Assert.False(result.Succeeded);
        Assert.Equal("AnotherActiveInvitationExists", result.FailureCode);
    }

    [Fact]
    public async Task ReplaceAsync_Rolls_Back_When_Insert_Fails_And_Keeps_Existing_Invitation_Active()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2));

        var forcedToken = "forced-collision-token";
        var forcedTokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(forcedToken))).ToLowerInvariant();

        var otherAssessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "other@example.com");
        await fixture.SeedInvitationAsync(otherAssessment.Id, "other@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2), tokenHash: forcedTokenHash);

        var service = new AssessmentInvitationIssuanceService(fixture.Db, () => forcedToken);

        var failed = await service.ReplaceAsync(assessment.Id, existing.Id, TimeSpan.FromHours(3));

        Assert.False(failed.Succeeded);
        Assert.Equal("ReplacementFailed", failed.FailureCode);

        var originalAfterFailure = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Null(originalAfterFailure.RevokedUtc);
    }

    [Fact]
    public async Task ReplaceAsync_Concurrent_Attempt_On_Same_Invitation_Fails_For_Second_Request()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2));

        var service = new AssessmentInvitationIssuanceService(fixture.Db);

        var first = await service.ReplaceAsync(assessment.Id, existing.Id, TimeSpan.FromHours(3));
        var second = await service.ReplaceAsync(assessment.Id, existing.Id, TimeSpan.FromHours(3));

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal("InvitationRevoked", second.FailureCode);
    }

    private sealed class TestDbFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public ApplicationDbContext Db { get; }

        private TestDbFixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public static async Task<TestDbFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;

            var db = new ApplicationDbContext(options);
            await db.Database.EnsureCreatedAsync();

            return new TestDbFixture(connection, db);
        }

        public async Task<Assessment> SeedAssessmentAsync(AssessmentStatus status, string contactEmail)
        {
            var assessment = new Assessment
            {
                Id = Guid.NewGuid(),
                Title = "Assessment",
                SiteName = "Site",
                ContactEmail = contactEmail,
                Status = status,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            Db.Set<Assessment>().Add(assessment);
            await Db.SaveChangesAsync();
            return assessment;
        }

        public async Task<AssessmentInvitation> SeedInvitationAsync(
            Guid assessmentId,
            string email,
            AssessmentInvitationDeliveryStatus deliveryStatus,
            DateTime expiresUtc,
            int accessCount = 0,
            DateTime? lastAccessUtc = null,
            DateTime? revokedUtc = null,
            string? tokenHash = null)
        {
            var invitation = new AssessmentInvitation
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                Email = email,
                TokenHash = tokenHash ?? Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = expiresUtc,
                RevokedUtc = revokedUtc,
                AccessCount = accessCount,
                LastAccessUtc = lastAccessUtc,
                DeliveryStatus = deliveryStatus,
                DeliveryAttemptCount = 0
            };

            Db.Set<AssessmentInvitation>().Add(invitation);
            await Db.SaveChangesAsync();
            return invitation;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
