using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentInvitationReplacementEligibilityServiceTests
{
    [Fact]
    public void Program_Registers_AssessmentInvitationReplacementEligibilityService_As_Scoped()
    {
        var programPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Program.cs"));
        Assert.True(File.Exists(programPath), $"Program.cs not found at '{programPath}'.");

        var programContent = File.ReadAllText(programPath);
        Assert.Matches(
            new Regex(@"AddScoped\s*<\s*IAssessmentInvitationReplacementEligibilityService\s*,\s*AssessmentInvitationReplacementEligibilityService\s*>\s*\(\s*\)\s*;", RegexOptions.CultureInvariant),
            programContent);
    }

    [Fact]
    public async Task EvaluateAsync_Returns_Eligible_When_All_Requirements_Are_Met()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var seeded = await fixture.SeedAssessmentWithInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            revokedUtc: null,
            accessCount: 0,
            lastAccessUtc: null,
            addResponse: false);

        var service = new AssessmentInvitationReplacementEligibilityService(fixture.Db);

        var result = await service.EvaluateAsync(seeded.AssessmentId, seeded.InvitationId);

        Assert.True(result.IsEligible);
        Assert.Null(result.FailureCode);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public async Task EvaluateAsync_Returns_Ineligible_For_Invalid_Request()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var service = new AssessmentInvitationReplacementEligibilityService(fixture.Db);

        var result = await service.EvaluateAsync(Guid.Empty, Guid.NewGuid());

        Assert.False(result.IsEligible);
        Assert.Equal("InvalidRequest", result.FailureCode);
    }

    [Fact]
    public async Task EvaluateAsync_Returns_Ineligible_When_Assessment_Not_Found()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var service = new AssessmentInvitationReplacementEligibilityService(fixture.Db);

        var result = await service.EvaluateAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(result.IsEligible);
        Assert.Equal("AssessmentNotFound", result.FailureCode);
    }

    [Theory]
    [InlineData(AssessmentStatus.InProgress)]
    [InlineData(AssessmentStatus.Submitted)]
    [InlineData(AssessmentStatus.Completed)]
    [InlineData(AssessmentStatus.Cancelled)]
    public async Task EvaluateAsync_Returns_Ineligible_For_NonInvited_Assessment_Status(AssessmentStatus status)
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var seeded = await fixture.SeedAssessmentWithInvitationAsync(
            assessmentStatus: status,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            revokedUtc: null,
            accessCount: 0,
            lastAccessUtc: null,
            addResponse: false);

        var service = new AssessmentInvitationReplacementEligibilityService(fixture.Db);

        var result = await service.EvaluateAsync(seeded.AssessmentId, seeded.InvitationId);

        Assert.False(result.IsEligible);
        Assert.Equal("IneligibleAssessmentStatus", result.FailureCode);
    }

    [Fact]
    public async Task EvaluateAsync_Returns_Ineligible_When_Invitation_Not_Found()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var seeded = await fixture.SeedAssessmentWithInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            revokedUtc: null,
            accessCount: 0,
            lastAccessUtc: null,
            addResponse: false);

        var service = new AssessmentInvitationReplacementEligibilityService(fixture.Db);

        var result = await service.EvaluateAsync(seeded.AssessmentId, Guid.NewGuid());

        Assert.False(result.IsEligible);
        Assert.Equal("InvitationNotFound", result.FailureCode);
    }

    [Fact]
    public async Task EvaluateAsync_Returns_Ineligible_For_Assessment_Invitation_Mismatch()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var first = await fixture.SeedAssessmentWithInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            revokedUtc: null,
            accessCount: 0,
            lastAccessUtc: null,
            addResponse: false);

        var second = await fixture.SeedAssessmentWithInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            revokedUtc: null,
            accessCount: 0,
            lastAccessUtc: null,
            addResponse: false);

        var service = new AssessmentInvitationReplacementEligibilityService(fixture.Db);

        var result = await service.EvaluateAsync(first.AssessmentId, second.InvitationId);

        Assert.False(result.IsEligible);
        Assert.Equal("InvitationAssessmentMismatch", result.FailureCode);
    }

    [Fact]
    public async Task EvaluateAsync_Returns_Ineligible_For_Revoked_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var seeded = await fixture.SeedAssessmentWithInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            revokedUtc: DateTime.UtcNow,
            accessCount: 0,
            lastAccessUtc: null,
            addResponse: false);

        var service = new AssessmentInvitationReplacementEligibilityService(fixture.Db);

        var result = await service.EvaluateAsync(seeded.AssessmentId, seeded.InvitationId);

        Assert.False(result.IsEligible);
        Assert.Equal("InvitationRevoked", result.FailureCode);
    }

    [Theory]
    [InlineData(AssessmentInvitationDeliveryStatus.Sending)]
    [InlineData(AssessmentInvitationDeliveryStatus.OutcomeUnknown)]
    [InlineData(AssessmentInvitationDeliveryStatus.Accepted)]
    public async Task EvaluateAsync_Returns_Ineligible_For_Disallowed_Delivery_Statuses(AssessmentInvitationDeliveryStatus status)
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var seeded = await fixture.SeedAssessmentWithInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: status,
            revokedUtc: null,
            accessCount: 0,
            lastAccessUtc: null,
            addResponse: false);

        var service = new AssessmentInvitationReplacementEligibilityService(fixture.Db);

        var result = await service.EvaluateAsync(seeded.AssessmentId, seeded.InvitationId);

        Assert.False(result.IsEligible);
        Assert.Equal("IneligibleDeliveryStatus", result.FailureCode);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(0, "2026-01-01T00:00:00Z")]
    [InlineData(2, "2026-01-01T00:00:00Z")]
    public async Task EvaluateAsync_Returns_Ineligible_When_Invitation_Shows_Access(int accessCount, string? lastAccessIso)
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var lastAccessUtc = lastAccessIso is null ? (DateTime?)null : DateTime.Parse(lastAccessIso, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var seeded = await fixture.SeedAssessmentWithInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            revokedUtc: null,
            accessCount: accessCount,
            lastAccessUtc: lastAccessUtc,
            addResponse: false);

        var service = new AssessmentInvitationReplacementEligibilityService(fixture.Db);

        var result = await service.EvaluateAsync(seeded.AssessmentId, seeded.InvitationId);

        Assert.False(result.IsEligible);
        Assert.Equal("InvitationAlreadyUsed", result.FailureCode);
    }

    [Fact]
    public async Task EvaluateAsync_Returns_Ineligible_When_AssessmentResponse_Exists_For_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var seeded = await fixture.SeedAssessmentWithInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            revokedUtc: null,
            accessCount: 0,
            lastAccessUtc: null,
            addResponse: true);

        var service = new AssessmentInvitationReplacementEligibilityService(fixture.Db);

        var result = await service.EvaluateAsync(seeded.AssessmentId, seeded.InvitationId);

        Assert.False(result.IsEligible);
        Assert.Equal("InvitationAlreadyUsed", result.FailureCode);
    }

    [Fact]
    public async Task EvaluateAsync_Returns_Ineligible_When_Another_Active_Invitation_Exists()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var seeded = await fixture.SeedAssessmentWithInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            revokedUtc: null,
            accessCount: 0,
            lastAccessUtc: null,
            addResponse: false);

        fixture.Db.Set<AssessmentInvitation>().Add(new AssessmentInvitation
        {
            Id = Guid.NewGuid(),
            AssessmentId = seeded.AssessmentId,
            Email = "another@example.com",
            TokenHash = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddHours(2),
            RevokedUtc = null,
            DeliveryStatus = AssessmentInvitationDeliveryStatus.Pending,
            DeliveryAttemptCount = 0
        });
        await fixture.Db.SaveChangesAsync();

        var service = new AssessmentInvitationReplacementEligibilityService(fixture.Db);

        var result = await service.EvaluateAsync(seeded.AssessmentId, seeded.InvitationId);

        Assert.False(result.IsEligible);
        Assert.Equal("AnotherActiveInvitationExists", result.FailureCode);
    }

    [Fact]
    public async Task EvaluateAsync_Allows_Failed_Delivery_When_Other_Requirements_Are_Met()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var seeded = await fixture.SeedAssessmentWithInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Failed,
            revokedUtc: null,
            accessCount: 0,
            lastAccessUtc: null,
            addResponse: false);

        var service = new AssessmentInvitationReplacementEligibilityService(fixture.Db);

        var result = await service.EvaluateAsync(seeded.AssessmentId, seeded.InvitationId);

        Assert.True(result.IsEligible);
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

        public async Task<(Guid AssessmentId, Guid InvitationId)> SeedAssessmentWithInvitationAsync(
            AssessmentStatus assessmentStatus,
            AssessmentInvitationDeliveryStatus deliveryStatus,
            DateTime? revokedUtc,
            int accessCount,
            DateTime? lastAccessUtc,
            bool addResponse)
        {
            var assessmentId = Guid.NewGuid();
            var invitationId = Guid.NewGuid();

            Db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "Assessment",
                SiteName = "Site",
                ContactEmail = "customer@example.com",
                Status = assessmentStatus,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            Db.Set<AssessmentInvitation>().Add(new AssessmentInvitation
            {
                Id = invitationId,
                AssessmentId = assessmentId,
                Email = "customer@example.com",
                TokenHash = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddHours(4),
                RevokedUtc = revokedUtc,
                AccessCount = accessCount,
                LastAccessUtc = lastAccessUtc,
                DeliveryStatus = deliveryStatus,
                DeliveryAttemptCount = 0
            });

            if (addResponse)
            {
                Db.Set<AssessmentResponse>().Add(new AssessmentResponse
                {
                    Id = Guid.NewGuid(),
                    AssessmentId = assessmentId,
                    InvitationId = invitationId,
                    Data = "{}",
                    IsDraft = true,
                    LastSavedUtc = DateTime.UtcNow,
                    SubmittedUtc = null,
                    ConcurrencyStamp = Guid.NewGuid().ToString("N")
                });
            }

            await Db.SaveChangesAsync();

            return (assessmentId, invitationId);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
