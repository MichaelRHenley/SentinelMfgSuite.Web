using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentInvitationReplacementSendServiceTests
{
    [Fact]
    public async Task ReplaceAndSendAsync_Succeeds_And_Delivers_And_Preserves_Original_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2));

        var sender = new CapturingEmailSender();
        var service = CreateService(fixture.Db, sender);

        var result = await service.ReplaceAndSendAsync(assessment.Id, existing.Id);

        Assert.Equal(AssessmentInvitationReplacementSendOutcome.Delivered, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.NotNull(result.InvitationId);
        Assert.NotEqual(existing.Id, result.InvitationId!.Value);
        Assert.Equal(1, sender.SendCallCount);

        var original = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(i => i.Id == existing.Id);
        Assert.NotNull(original.RevokedUtc);
        Assert.Equal("1.0", (await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync(a => a.Id == assessment.Id)).QuestionnaireVersion);

        var replacement = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(i => i.Id == result.InvitationId.Value);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Accepted, replacement.DeliveryStatus);
        Assert.Null(replacement.RevokedUtc);
    }

    [Fact]
    public async Task ReplaceAndSendAsync_Does_Not_Create_Another_Assessment()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2));

        var service = CreateService(fixture.Db, new CapturingEmailSender());
        await service.ReplaceAndSendAsync(assessment.Id, existing.Id);

        var assessmentCount = await fixture.Db.Set<Assessment>().CountAsync();
        Assert.Equal(1, assessmentCount);
    }

    [Fact]
    public async Task ReplaceAndSendAsync_Sends_Email_Only_After_Replacement_Commits()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2));

        var sender = new CommitAwareEmailSender(fixture.Db);
        var service = CreateService(fixture.Db, sender);

        var result = await service.ReplaceAndSendAsync(assessment.Id, existing.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(1, sender.SendCallCount);
        Assert.True(sender.SawCommittedState);
    }

    [Fact]
    public async Task ReplaceAndSendAsync_When_Suppressed_Returns_EmailSuppressed()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2));

        var service = CreateService(fixture.Db, new DevelopmentSuppressingEmailSender());
        var result = await service.ReplaceAndSendAsync(assessment.Id, existing.Id);

        Assert.Equal(AssessmentInvitationReplacementSendOutcome.EmailSuppressed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal("DevelopmentEmailSuppressed", result.FailureCode);
        Assert.NotNull(result.InvitationId);

        var replacement = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(i => i.Id == result.InvitationId!.Value);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Pending, replacement.DeliveryStatus);
    }

    [Fact]
    public async Task ReplaceAndSendAsync_When_AnotherSenderSuppresses_DoesNotExposeDevelopmentToken()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2));
        var callbackCalled = false;
        var service = CreateService(fixture.Db, new OtherSuppressingEmailSender());

        var result = await service.ReplaceAndSendAsync(
            assessment.Id,
            existing.Id,
            onDevelopmentEmailSuppressed: (_, _) => callbackCalled = true);

        Assert.Equal(AssessmentInvitationReplacementSendOutcome.DeliveryFailed, result.Outcome);
        Assert.False(callbackCalled);
    }

    [Fact]
    public async Task ReplaceAndSendAsync_On_Provider_Failure_Returns_DeliveryFailed_And_Preserves_Records()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2));

        var service = CreateService(fixture.Db, new ThrowingEmailSender());
        var result = await service.ReplaceAndSendAsync(assessment.Id, existing.Id);

        Assert.Equal(AssessmentInvitationReplacementSendOutcome.DeliveryFailed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal("EmailDeliveryFailed", result.FailureCode);
        Assert.NotNull(result.InvitationId);

        var original = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(i => i.Id == existing.Id);
        Assert.NotNull(original.RevokedUtc);

        var replacement = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(i => i.Id == result.InvitationId!.Value);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Failed, replacement.DeliveryStatus);
    }

    private sealed class OtherSuppressingEmailSender : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
            => Task.FromResult(EmailSendResult.Suppressed());
    }

    [Theory]
    [InlineData(AssessmentInvitationDeliveryStatus.Sending)]
    [InlineData(AssessmentInvitationDeliveryStatus.OutcomeUnknown)]
    [InlineData(AssessmentInvitationDeliveryStatus.Accepted)]
    public async Task ReplaceAndSendAsync_Rejects_Ineligible_Invitation_Without_Attempting_Delivery(AssessmentInvitationDeliveryStatus deliveryStatus)
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", deliveryStatus, DateTime.UtcNow.AddHours(2));

        var sender = new CapturingEmailSender();
        var service = CreateService(fixture.Db, sender);

        var result = await service.ReplaceAndSendAsync(assessment.Id, existing.Id);

        Assert.Equal(AssessmentInvitationReplacementSendOutcome.ReplacementFailed, result.Outcome);
        Assert.Equal("IneligibleDeliveryStatus", result.FailureCode);
        Assert.Equal(0, sender.SendCallCount);
    }

    [Fact]
    public async Task ReplaceAndSendAsync_Rejects_Used_Invitation()
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

        var service = CreateService(fixture.Db, new CapturingEmailSender());
        var result = await service.ReplaceAndSendAsync(assessment.Id, existing.Id);

        Assert.Equal(AssessmentInvitationReplacementSendOutcome.ReplacementFailed, result.Outcome);
        Assert.Equal("InvitationAlreadyUsed", result.FailureCode);
    }

    [Fact]
    public async Task ReplaceAndSendAsync_Concurrent_Attempts_Only_One_Succeeds()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2));

        var sender = new CapturingEmailSender();
        var service = CreateService(fixture.Db, sender);

        var first = await service.ReplaceAndSendAsync(assessment.Id, existing.Id);
        var second = await service.ReplaceAndSendAsync(assessment.Id, existing.Id);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(AssessmentInvitationReplacementSendOutcome.ReplacementFailed, second.Outcome);
        Assert.Equal("InvitationRevoked", second.FailureCode);
        Assert.Equal(1, sender.SendCallCount);
    }

    [Fact]
    public async Task ReplaceAndSendAsync_Does_Not_Leak_Token()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = await fixture.SeedAssessmentAsync(AssessmentStatus.Invited, "customer@example.com");
        var existing = await fixture.SeedInvitationAsync(assessment.Id, "customer@example.com", AssessmentInvitationDeliveryStatus.Pending, DateTime.UtcNow.AddHours(2));

        var service = CreateService(fixture.Db, new CapturingEmailSender());
        var result = await service.ReplaceAndSendAsync(assessment.Id, existing.Id);

        var resultText = result.ToString() ?? string.Empty;
        Assert.DoesNotContain("InvitationToken", resultText, StringComparison.Ordinal);
    }

    private static AssessmentInvitationReplacementSendService CreateService(ApplicationDbContext db, IEmailSender sender)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppUrls:Base"] = "https://sentinelaisystems.net"
            })
            .Build();

        var issuanceService = new AssessmentInvitationIssuanceService(db);
        var deliveryService = new AssessmentInvitationDeliveryService(db, sender, config);
        return new AssessmentInvitationReplacementSendService(db, issuanceService, deliveryService);
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public int SendCallCount { get; private set; }

        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
        {
            SendCallCount++;
            return Task.FromResult(EmailSendResult.Accepted());
        }
    }

    private sealed class CommitAwareEmailSender : IEmailSender
    {
        private readonly ApplicationDbContext _db;

        public CommitAwareEmailSender(ApplicationDbContext db)
        {
            _db = db;
        }

        public int SendCallCount { get; private set; }
        public bool SawCommittedState { get; private set; }

        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
        {
            SendCallCount++;
            SawCommittedState = _db.Database.CurrentTransaction is null;
            return Task.FromResult(EmailSendResult.Accepted());
        }
    }

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
            => throw new InvalidOperationException("Simulated provider failure");
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
            DateTime? revokedUtc = null)
        {
            var invitation = new AssessmentInvitation
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                Email = email,
                TokenHash = Guid.NewGuid().ToString("N"),
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
