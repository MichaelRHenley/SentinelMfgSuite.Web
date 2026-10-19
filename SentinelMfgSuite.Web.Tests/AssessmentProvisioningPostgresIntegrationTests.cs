using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services;
using SentinelMfgSuite.Web.Services.Assessments;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentProvisioningPostgresIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ProvisionAsync_For_ManualAdmin_Succeeds_And_Accepts_Email_After_Commit()
    {
        var fixture = CreateFixtureOrThrow();
        await fixture.InitializeAsync();

        var recordIds = new DisposableRecordIds();
        try
        {
            var request = new AssessmentProvisioningRequest(
                AssessmentProvisioningInitiationSource.ManualAdmin,
                "Atomic Provisioning",
                "PG Integration Site",
                $"integration+{Guid.NewGuid():N}@example.com");

            await using var db = fixture.CreateDbContext();
            var sender = new CommitAwareEmailSender(db);
            var deliveryService = new AssessmentInvitationDeliveryService(db, sender, CreateConfig("https://sentinelaisystems.net"));
            var issuanceParticipant = new AssessmentInvitationIssuanceService(db);
            var service = new AssessmentProvisioningService(db, deliveryService, issuanceParticipant);

            var result = await service.ProvisionAsync(request);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.AssessmentId);
            Assert.NotNull(result.InvitationId);
            Assert.Null(result.FailureCode);
            Assert.Equal(1, sender.SendCallCount);
            Assert.True(sender.SawCommittedState);
            Assert.NotNull(sender.LastBody);

            var token = ExtractInvitationToken(sender.LastBody!);
            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.DoesNotContain(token, result.ToString(), StringComparison.Ordinal);

            recordIds.AssessmentIds.Add(result.AssessmentId!.Value);
            recordIds.InvitationIds.Add(result.InvitationId!.Value);

            var assessment = await db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == result.AssessmentId.Value);
            var invitation = await db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == result.InvitationId.Value);

            Assert.Equal(assessment.Id, result.AssessmentId);
            Assert.Equal(invitation.Id, result.InvitationId);
            Assert.Equal(AssessmentStatus.Invited, assessment.Status);
            Assert.Equal(assessment.Id, invitation.AssessmentId);
            Assert.Equal(AssessmentInvitationDeliveryStatus.Accepted, invitation.DeliveryStatus);
            Assert.NotNull(invitation.DeliveryAcceptedUtc);
        }
        finally
        {
            await CleanupAsync(fixture, recordIds);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ProvisionAsync_For_ManualAdmin_Preserves_Assessment_And_Invitation_When_Email_Delivery_Fails()
    {
        var fixture = CreateFixtureOrThrow();
        await fixture.InitializeAsync();

        var recordIds = new DisposableRecordIds();
        try
        {
            var request = new AssessmentProvisioningRequest(
                AssessmentProvisioningInitiationSource.ManualAdmin,
                "Atomic Provisioning",
                "PG Integration Site",
                $"integration+{Guid.NewGuid():N}@example.com");

            await using var db = fixture.CreateDbContext();
            var sender = new ThrowingEmailSender();
            var deliveryService = new AssessmentInvitationDeliveryService(db, sender, CreateConfig("https://sentinelaisystems.net"));
            var issuanceParticipant = new AssessmentInvitationIssuanceService(db);
            var service = new AssessmentProvisioningService(db, deliveryService, issuanceParticipant);

            var result = await service.ProvisionAsync(request);

            Assert.False(result.Succeeded);
            Assert.Equal("EmailDeliveryFailed", result.FailureCode);
            Assert.NotNull(result.AssessmentId);
            Assert.NotNull(result.InvitationId);

            recordIds.AssessmentIds.Add(result.AssessmentId!.Value);
            recordIds.InvitationIds.Add(result.InvitationId!.Value);

            var assessment = await db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == result.AssessmentId.Value);
            var invitation = await db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == result.InvitationId.Value);

            Assert.Equal(assessment.Id, result.AssessmentId);
            Assert.Equal(invitation.Id, result.InvitationId);
            Assert.Equal(AssessmentStatus.Invited, assessment.Status);
            Assert.Equal(assessment.Id, invitation.AssessmentId);
            Assert.Equal(AssessmentInvitationDeliveryStatus.Failed, invitation.DeliveryStatus);
            Assert.Equal(1, invitation.DeliveryAttemptCount);
            Assert.Equal("EmailProviderFailure", invitation.DeliveryFailureCode);
            Assert.Null(invitation.DeliveryAcceptedUtc);
        }
        finally
        {
            await CleanupAsync(fixture, recordIds);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ProvisionManualWithInvitationAtomicAsync_Persists_Assessment_And_Invitation_In_Single_Transaction()
    {
        var fixture = CreateFixtureOrThrow();
        await fixture.InitializeAsync();

        var recordIds = new DisposableRecordIds();
        try
        {
            var request = new AssessmentProvisioningRequest(
                AssessmentProvisioningInitiationSource.ManualAdmin,
                "Atomic Provisioning",
                "PG Integration Site",
                $"integration+{Guid.NewGuid():N}@example.com");

            AssessmentManualProvisioningAtomicResult result;
            await using (var db = fixture.CreateDbContext())
            {
                var participant = new AssessmentInvitationIssuanceService(db);
                var service = new AssessmentProvisioningService(db, participant);
                result = await service.ProvisionManualWithInvitationAtomicAsync(request, TimeSpan.FromHours(12));
            }

            Assert.True(result.Succeeded);
            Assert.NotNull(result.AssessmentId);
            Assert.NotNull(result.InvitationId);
            Assert.False(string.IsNullOrWhiteSpace(result.InvitationToken));

            recordIds.AssessmentIds.Add(result.AssessmentId!.Value);
            recordIds.InvitationIds.Add(result.InvitationId!.Value);

            await using var verifyDb = fixture.CreateDbContext();
            var assessment = await verifyDb.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == result.AssessmentId.Value);
            var invitation = await verifyDb.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == result.InvitationId.Value);

            Assert.Equal(AssessmentStatus.Invited, assessment.Status);
            Assert.Equal(assessment.Id, invitation.AssessmentId);
            Assert.Null(invitation.RevokedUtc);
        }
        finally
        {
            await CleanupAsync(fixture, recordIds);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ProvisionManualWithInvitationAtomicAsync_Rolls_Back_Assessment_When_Issuance_Fails()
    {
        var fixture = CreateFixtureOrThrow();
        await fixture.InitializeAsync();

        var uniqueEmail = $"integration+{Guid.NewGuid():N}@example.com";
        var request = new AssessmentProvisioningRequest(
            AssessmentProvisioningInitiationSource.ManualAdmin,
            "Atomic Provisioning",
            "PG Integration Site",
            uniqueEmail);

        var failureParticipant = new FailingIssuanceParticipant();

        AssessmentManualProvisioningAtomicResult result;
        await using (var db = fixture.CreateDbContext())
        {
            var service = new AssessmentProvisioningService(db, failureParticipant);
            result = await service.ProvisionManualWithInvitationAtomicAsync(request, TimeSpan.FromHours(12));
        }

        Assert.False(result.Succeeded);
        Assert.Equal("DuplicateActiveInvitation", result.FailureCode);

        await using var verifyDb = fixture.CreateDbContext();
        var assessmentExists = await verifyDb.Set<Assessment>().AsNoTracking().AnyAsync(x => x.ContactEmail == uniqueEmail);
        var invitationExists = await verifyDb.Set<AssessmentInvitation>().AsNoTracking().AnyAsync(x => x.Email == uniqueEmail);
        Assert.False(assessmentExists);
        Assert.False(invitationExists);
    }

    private static IConfiguration CreateConfig(string baseUrl)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppUrls:Base"] = baseUrl
            })
            .Build();
    }

    private static string ExtractInvitationToken(string htmlBody)
    {
        var match = Regex.Match(htmlBody, "token=([^\"'&<\\s]+)", RegexOptions.CultureInvariant);
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : string.Empty;
    }

    private static PostgresIntegrationTestFixture CreateFixtureOrThrow()
    {
        var connection = Environment.GetEnvironmentVariable(PostgresIntegrationTestFixture.ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException($"Set {PostgresIntegrationTestFixture.ConnectionEnvironmentVariable} to run PostgreSQL integration tests.");
        }

        return new PostgresIntegrationTestFixture();
    }

    private static async Task CleanupAsync(PostgresIntegrationTestFixture fixture, DisposableRecordIds ids)
    {
        await using var db = fixture.CreateDbContext();

        if (ids.ResponseIds.Count > 0)
        {
            await db.Set<AssessmentResponse>()
                .Where(x => ids.ResponseIds.Contains(x.Id))
                .ExecuteDeleteAsync();
        }

        if (ids.InvitationIds.Count > 0)
        {
            await db.Set<AssessmentInvitation>()
                .Where(x => ids.InvitationIds.Contains(x.Id))
                .ExecuteDeleteAsync();
        }

        if (ids.AssessmentIds.Count > 0)
        {
            await db.Set<Assessment>()
                .Where(x => ids.AssessmentIds.Contains(x.Id))
                .ExecuteDeleteAsync();
        }
    }

    private sealed class DisposableRecordIds
    {
        public List<Guid> AssessmentIds { get; } = new();
        public List<Guid> InvitationIds { get; } = new();
        public List<Guid> ResponseIds { get; } = new();
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

        public string? LastBody { get; private set; }

        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
        {
            SendCallCount++;
            SawCommittedState = _db.Database.CurrentTransaction is null;
            LastBody = htmlBody;
            return Task.FromResult(EmailSendResult.Accepted());
        }
    }

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
            => throw new InvalidOperationException("Simulated provider failure");
    }

    private sealed class FailingIssuanceParticipant : IAssessmentInvitationIssuanceTransactionParticipant
    {
        public Task<AssessmentInvitationIssuanceResult> IssueWithinExistingTransactionAsync(Guid assessmentId, TimeSpan expiresIn, CancellationToken ct = default)
            => Task.FromResult(AssessmentInvitationIssuanceResult.Failure("DuplicateActiveInvitation", "Simulated failure"));
    }
}
