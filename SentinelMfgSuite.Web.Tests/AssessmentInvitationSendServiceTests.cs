using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentInvitationSendServiceTests
{
    [Fact]
    public async Task SendInvitationAsync_Issues_And_Delivers_For_Existing_Draft_Assessment()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessmentId = await fixture.SeedDraftAssessmentAsync("customer@example.com");
        var sender = new CapturingEmailSender();
        var service = CreateService(fixture.Db, sender);

        var result = await service.SendInvitationAsync(assessmentId);

        Assert.Equal(AssessmentInvitationSendOutcome.Delivered, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.NotNull(result.InvitationId);
        Assert.Equal(1, sender.SendCallCount);

        var assessment = await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync(a => a.Id == assessmentId);
        Assert.Equal(AssessmentStatus.Invited, assessment.Status);

        var invitation = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(i => i.AssessmentId == assessmentId);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Accepted, invitation.DeliveryStatus);
    }

    private sealed class SuppressingEmailSender : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
            => Task.FromResult(EmailSendResult.Suppressed());
    }

    [Fact]
    public async Task SendInvitationAsync_Does_Not_Create_Another_Assessment()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessmentId = await fixture.SeedDraftAssessmentAsync("customer@example.com");
        var sender = new CapturingEmailSender();
        var service = CreateService(fixture.Db, sender);

        await service.SendInvitationAsync(assessmentId);

        var assessmentCount = await fixture.Db.Set<Assessment>().CountAsync();
        Assert.Equal(1, assessmentCount);
    }

    [Fact]
    public async Task SendInvitationAsync_Sends_Email_Only_After_Issuance_Commits()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessmentId = await fixture.SeedDraftAssessmentAsync("customer@example.com");
        var sender = new CommitAwareEmailSender(fixture.Db);
        var service = CreateService(fixture.Db, sender);

        var result = await service.SendInvitationAsync(assessmentId);

        Assert.True(result.Succeeded);
        Assert.Equal(1, sender.SendCallCount);
        Assert.True(sender.SawCommittedState);
    }

    [Fact]
    public async Task SendInvitationAsync_When_Suppressed_Returns_EmailSuppressed_And_Invitation_Remains_Pending()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessmentId = await fixture.SeedDraftAssessmentAsync("customer@example.com");
        var sender = new DevelopmentSuppressingEmailSender();
        var service = CreateService(fixture.Db, sender);

        var result = await service.SendInvitationAsync(assessmentId);

        Assert.Equal(AssessmentInvitationSendOutcome.EmailSuppressed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal("DevelopmentEmailSuppressed", result.FailureCode);
        Assert.NotNull(result.InvitationId);

        var invitation = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(i => i.AssessmentId == assessmentId);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Pending, invitation.DeliveryStatus);
        Assert.Null(invitation.DeliveryAcceptedUtc);

        // Assessment remains Invited (issuance committed); it is not reverted by suppression.
        var assessment = await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync(a => a.Id == assessmentId);
        Assert.Equal(AssessmentStatus.Invited, assessment.Status);
    }

    [Fact]
    public async Task SendInvitationAsync_On_Provider_Failure_Returns_DeliveryFailed_And_Preserves_Records()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessmentId = await fixture.SeedDraftAssessmentAsync("customer@example.com");
        var sender = new ThrowingEmailSender();
        var service = CreateService(fixture.Db, sender);

        var result = await service.SendInvitationAsync(assessmentId);

        Assert.Equal(AssessmentInvitationSendOutcome.DeliveryFailed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal("EmailDeliveryFailed", result.FailureCode);
        Assert.NotNull(result.InvitationId);

        var invitation = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(i => i.AssessmentId == assessmentId);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Failed, invitation.DeliveryStatus);

        var assessment = await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync(a => a.Id == assessmentId);
        Assert.Equal(AssessmentStatus.Invited, assessment.Status);
    }

    [Fact]
    public async Task SendInvitationAsync_Rejects_NonDraft_Assessment()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessmentId = await fixture.SeedAssessmentAsync("customer@example.com", AssessmentStatus.Invited);
        var sender = new CapturingEmailSender();
        var service = CreateService(fixture.Db, sender);

        var result = await service.SendInvitationAsync(assessmentId);

        Assert.Equal(AssessmentInvitationSendOutcome.IssuanceFailed, result.Outcome);
        Assert.Equal("IneligibleAssessmentStatus", result.FailureCode);
        Assert.Equal(0, sender.SendCallCount);
    }

    [Fact]
    public async Task SendInvitationAsync_Rejects_Duplicate_Concurrent_Submission()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessmentId = await fixture.SeedDraftAssessmentAsync("customer@example.com");
        var sender = new CapturingEmailSender();
        var service = CreateService(fixture.Db, sender);

        var first = await service.SendInvitationAsync(assessmentId);
        Assert.True(first.Succeeded);

        var second = await service.SendInvitationAsync(assessmentId);

        Assert.Equal(AssessmentInvitationSendOutcome.IssuanceFailed, second.Outcome);
        Assert.Equal("IneligibleAssessmentStatus", second.FailureCode);
        Assert.Equal(1, sender.SendCallCount);
    }

    [Fact]
    public async Task SendInvitationAsync_Does_Not_Leak_Token()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessmentId = await fixture.SeedDraftAssessmentAsync("customer@example.com");
        var sender = new CapturingEmailSender();
        var service = CreateService(fixture.Db, sender);

        var result = await service.SendInvitationAsync(assessmentId);

        var resultText = result.ToString() ?? string.Empty;
        Assert.DoesNotContain("InvitationToken", resultText, StringComparison.Ordinal);
    }

    private static AssessmentInvitationSendService CreateService(ApplicationDbContext db, IEmailSender sender)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppUrls:Base"] = "https://sentinelaisystems.net"
            })
            .Build();

        var issuanceService = new AssessmentInvitationIssuanceService(db);
        var deliveryService = new AssessmentInvitationDeliveryService(db, sender, config);
        return new AssessmentInvitationSendService(db, issuanceService, deliveryService);
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

        public async Task<Guid> SeedDraftAssessmentAsync(string email)
            => await SeedAssessmentAsync(email, AssessmentStatus.Draft);

        public async Task<Guid> SeedAssessmentAsync(string email, AssessmentStatus status)
        {
            var assessmentId = Guid.NewGuid();

            Db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "Assessment",
                SiteName = "Site",
                ContactEmail = email,
                Status = status,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            await Db.SaveChangesAsync();

            return assessmentId;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
