using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentInvitationDeliveryServiceTests
{
    [Fact]
    public void Program_Registers_AssessmentInvitationDeliveryService_As_Scoped()
    {
        var programPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Program.cs"));
        Assert.True(File.Exists(programPath), $"Program.cs not found at '{programPath}'.");

        var programContent = File.ReadAllText(programPath);
        Assert.Matches(
            new Regex(@"AddScoped\s*<\s*IAssessmentInvitationDeliveryService\s*,\s*AssessmentInvitationDeliveryService\s*>\s*\(\s*\)\s*;", RegexOptions.CultureInvariant),
            programContent);
    }

    [Fact]
    public void Request_Assigns_Required_Fields_And_Trims_String_Values()
    {
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var expiresUtc = DateTime.UtcNow.AddDays(7);

        var request = new AssessmentInvitationDeliveryRequest(
            assessmentId,
            invitationId,
            "  customer@example.com  ",
            "  raw-token-value  ",
            expiresUtc);

        Assert.Equal(assessmentId, request.AssessmentId);
        Assert.Equal(invitationId, request.InvitationId);
        Assert.Equal("customer@example.com", request.RecipientEmail);
        Assert.Equal("raw-token-value", request.RawInvitationToken);
        Assert.Equal(expiresUtc, request.ExpiresUtc);
    }

    [Theory]
    [InlineData("", "token")]
    [InlineData("customer@example.com", "")]
    public void Request_Throws_For_Missing_Required_String_Values(string email, string token)
    {
        Assert.Throws<ArgumentException>(() => new AssessmentInvitationDeliveryRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            email,
            token,
            DateTime.UtcNow.AddDays(7)));
    }

    [Fact]
    public void Request_Throws_For_Empty_AssessmentId()
    {
        Assert.Throws<ArgumentException>(() => new AssessmentInvitationDeliveryRequest(
            Guid.Empty,
            Guid.NewGuid(),
            "customer@example.com",
            "token",
            DateTime.UtcNow.AddDays(7)));
    }

    [Fact]
    public void Request_Throws_For_Empty_InvitationId()
    {
        Assert.Throws<ArgumentException>(() => new AssessmentInvitationDeliveryRequest(
            Guid.NewGuid(),
            Guid.Empty,
            "customer@example.com",
            "token",
            DateTime.UtcNow.AddDays(7)));
    }

    [Fact]
    public void Request_Throws_For_Default_ExpiresUtc()
    {
        Assert.Throws<ArgumentException>(() => new AssessmentInvitationDeliveryRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "customer@example.com",
            "token",
            default));
    }

    [Fact]
    public void DeliveryResult_Does_Not_Expose_RawToken()
    {
        var hasTokenProperty = typeof(AssessmentInvitationDeliveryResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));

        Assert.False(hasTokenProperty);
    }

    [Fact]
    public async Task DeliverAsync_Successfully_Transitions_Pending_To_Accepted_And_Sends_Email()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var fakeSender = new CapturingEmailSender();
        var config = CreateConfig("https://sentinelaisystems.net");

        var seeded = await fixture.SeedInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            expiresUtc: DateTime.UtcNow.AddHours(4),
            revokedUtc: null,
            email: "customer@example.com");

        var service = new AssessmentInvitationDeliveryService(fixture.Db, fakeSender, config);

        var request = new AssessmentInvitationDeliveryRequest(
            seeded.AssessmentId,
            seeded.InvitationId,
            "customer@example.com",
            "raw token+with/special?chars",
            seeded.ExpiresUtc);

        var result = await service.DeliverAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(1, fakeSender.SendCallCount);
        Assert.Equal("customer@example.com", fakeSender.LastToEmail);
        Assert.Contains("Industrial Data & AI Readiness Assessment", fakeSender.LastSubject, StringComparison.Ordinal);

        var encodedToken = Uri.EscapeDataString(request.RawInvitationToken);
        Assert.Contains($"/assessment/questionnaire/{request.InvitationId:D}?token={encodedToken}", fakeSender.LastBody!, StringComparison.Ordinal);
        Assert.Contains("Sentinel AI Systems", fakeSender.LastBody!, StringComparison.Ordinal);

        var saved = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == seeded.InvitationId);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Accepted, saved.DeliveryStatus);
        Assert.Equal(1, saved.DeliveryAttemptCount);
        Assert.NotNull(saved.DeliveryLastAttemptUtc);
        Assert.NotNull(saved.DeliveryAcceptedUtc);
        Assert.Null(saved.DeliveryFailureCode);
        Assert.DoesNotContain("raw token+with/special?chars", saved.TokenHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliverAsync_On_Email_Provider_Failure_Transitions_To_Failed()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var sender = new ThrowingEmailSender();
        var config = CreateConfig("https://sentinelaisystems.net");

        var seeded = await fixture.SeedInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            expiresUtc: DateTime.UtcNow.AddHours(4),
            revokedUtc: null,
            email: "customer@example.com");

        var service = new AssessmentInvitationDeliveryService(fixture.Db, sender, config);

        var result = await service.DeliverAsync(new AssessmentInvitationDeliveryRequest(
            seeded.AssessmentId,
            seeded.InvitationId,
            "customer@example.com",
            "token",
            seeded.ExpiresUtc));

        Assert.False(result.Succeeded);
        Assert.Equal("EmailDeliveryFailed", result.FailureCode);
        Assert.Equal("Invitation delivery failed. Please retry or contact support.", result.FailureMessage);

        var saved = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == seeded.InvitationId);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Failed, saved.DeliveryStatus);
        Assert.Equal(1, saved.DeliveryAttemptCount);
        Assert.NotNull(saved.DeliveryLastAttemptUtc);
        Assert.Null(saved.DeliveryAcceptedUtc);
        Assert.Equal("EmailProviderFailure", saved.DeliveryFailureCode);
    }

    [Fact]
    public async Task DeliverAsync_Claim_Failure_Does_Not_Send_Email()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var sender = new CapturingEmailSender();
        var config = CreateConfig("https://sentinelaisystems.net");

        var seeded = await fixture.SeedInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Sending,
            expiresUtc: DateTime.UtcNow.AddHours(4),
            revokedUtc: null,
            email: "customer@example.com");

        var service = new AssessmentInvitationDeliveryService(fixture.Db, sender, config);

        var result = await service.DeliverAsync(new AssessmentInvitationDeliveryRequest(
            seeded.AssessmentId,
            seeded.InvitationId,
            "customer@example.com",
            "token",
            seeded.ExpiresUtc));

        Assert.False(result.Succeeded);
        Assert.Equal("DeliveryAlreadyClaimed", result.FailureCode);
        Assert.Equal(0, sender.SendCallCount);
    }

    [Fact]
    public async Task DeliverAsync_OutcomeUnknown_Returns_DeliveryOutcomeUnknown_And_Does_Not_Send_Email()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var sender = new CapturingEmailSender();
        var config = CreateConfig("https://sentinelaisystems.net");

        var seeded = await fixture.SeedInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.OutcomeUnknown,
            expiresUtc: DateTime.UtcNow.AddHours(4),
            revokedUtc: null,
            email: "customer@example.com");

        var service = new AssessmentInvitationDeliveryService(fixture.Db, sender, config);

        var result = await service.DeliverAsync(new AssessmentInvitationDeliveryRequest(
            seeded.AssessmentId,
            seeded.InvitationId,
            "customer@example.com",
            "token",
            seeded.ExpiresUtc));

        Assert.False(result.Succeeded);
        Assert.Equal("DeliveryOutcomeUnknown", result.FailureCode);
        Assert.Equal(0, sender.SendCallCount);

        var saved = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == seeded.InvitationId);
        Assert.Equal(AssessmentInvitationDeliveryStatus.OutcomeUnknown, saved.DeliveryStatus);
        Assert.Equal(0, saved.DeliveryAttemptCount);
        Assert.Null(saved.DeliveryLastAttemptUtc);
        Assert.Null(saved.DeliveryAcceptedUtc);
    }

    [Fact]
    public async Task DeliverAsync_Rejects_Revoked_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var sender = new CapturingEmailSender();
        var config = CreateConfig("https://sentinelaisystems.net");

        var seeded = await fixture.SeedInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            expiresUtc: DateTime.UtcNow.AddHours(4),
            revokedUtc: DateTime.UtcNow.AddMinutes(-1),
            email: "customer@example.com");

        var service = new AssessmentInvitationDeliveryService(fixture.Db, sender, config);

        var result = await service.DeliverAsync(new AssessmentInvitationDeliveryRequest(
            seeded.AssessmentId,
            seeded.InvitationId,
            "customer@example.com",
            "token",
            seeded.ExpiresUtc));

        Assert.False(result.Succeeded);
        Assert.Equal("InvitationRevoked", result.FailureCode);
        Assert.Equal(0, sender.SendCallCount);
    }

    [Fact]
    public async Task DeliverAsync_Rejects_Expired_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var sender = new CapturingEmailSender();
        var config = CreateConfig("https://sentinelaisystems.net");

        var seeded = await fixture.SeedInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            expiresUtc: DateTime.UtcNow.AddSeconds(-5),
            revokedUtc: null,
            email: "customer@example.com");

        var service = new AssessmentInvitationDeliveryService(fixture.Db, sender, config);

        var result = await service.DeliverAsync(new AssessmentInvitationDeliveryRequest(
            seeded.AssessmentId,
            seeded.InvitationId,
            "customer@example.com",
            "token",
            seeded.ExpiresUtc));

        Assert.False(result.Succeeded);
        Assert.Equal("InvitationExpired", result.FailureCode);
        Assert.Equal(0, sender.SendCallCount);
    }

    [Fact]
    public async Task DeliverAsync_Rejects_Ineligible_Assessment()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var sender = new CapturingEmailSender();
        var config = CreateConfig("https://sentinelaisystems.net");

        var seeded = await fixture.SeedInvitationAsync(
            assessmentStatus: AssessmentStatus.Cancelled,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            expiresUtc: DateTime.UtcNow.AddHours(2),
            revokedUtc: null,
            email: "customer@example.com");

        var service = new AssessmentInvitationDeliveryService(fixture.Db, sender, config);

        var result = await service.DeliverAsync(new AssessmentInvitationDeliveryRequest(
            seeded.AssessmentId,
            seeded.InvitationId,
            "customer@example.com",
            "token",
            seeded.ExpiresUtc));

        Assert.False(result.Succeeded);
        Assert.Equal("IneligibleAssessment", result.FailureCode);
        Assert.Equal(0, sender.SendCallCount);
    }

    [Fact]
    public async Task DeliverAsync_Does_Not_Expose_Token_In_Result()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var sender = new CapturingEmailSender();
        var config = CreateConfig("https://sentinelaisystems.net");

        var seeded = await fixture.SeedInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            expiresUtc: DateTime.UtcNow.AddHours(4),
            revokedUtc: null,
            email: "customer@example.com");

        var service = new AssessmentInvitationDeliveryService(fixture.Db, sender, config);
        var token = "top-secret-token";

        var result = await service.DeliverAsync(new AssessmentInvitationDeliveryRequest(
            seeded.AssessmentId,
            seeded.InvitationId,
            "customer@example.com",
            token,
            seeded.ExpiresUtc));

        var resultText = result.ToString();
        Assert.DoesNotContain(token, resultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliverAsync_Returns_Failure_For_Missing_Base_Url_And_Does_Not_Send_Email()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var sender = new CapturingEmailSender();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        var seeded = await fixture.SeedInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            expiresUtc: DateTime.UtcNow.AddHours(4),
            revokedUtc: null,
            email: "customer@example.com");

        var service = new AssessmentInvitationDeliveryService(fixture.Db, sender, config);

        var result = await service.DeliverAsync(new AssessmentInvitationDeliveryRequest(
            seeded.AssessmentId,
            seeded.InvitationId,
            "customer@example.com",
            "token",
            seeded.ExpiresUtc));

        Assert.False(result.Succeeded);
        Assert.Equal("MissingPublicBaseUrl", result.FailureCode);
        Assert.Equal(0, sender.SendCallCount);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://sentinelaisystems.net")]
    public async Task DeliverAsync_Returns_Failure_For_Invalid_Base_Url(string baseUrl)
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var sender = new CapturingEmailSender();
        var config = CreateConfig(baseUrl);

        var seeded = await fixture.SeedInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            expiresUtc: DateTime.UtcNow.AddHours(4),
            revokedUtc: null,
            email: "customer@example.com");

        var service = new AssessmentInvitationDeliveryService(fixture.Db, sender, config);

        var result = await service.DeliverAsync(new AssessmentInvitationDeliveryRequest(
            seeded.AssessmentId,
            seeded.InvitationId,
            "customer@example.com",
            "token",
            seeded.ExpiresUtc));

        Assert.False(result.Succeeded);
        Assert.Equal("InvalidPublicBaseUrl", result.FailureCode);
        Assert.Equal(0, sender.SendCallCount);
    }

    [Fact]
    public async Task DeliverAsync_When_Email_Suppressed_Reverts_Sending_To_Pending()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var sender = new DevelopmentSuppressingEmailSender();
        var config = CreateConfig("https://sentinelaisystems.net");

        var seeded = await fixture.SeedInvitationAsync(
            assessmentStatus: AssessmentStatus.Invited,
            deliveryStatus: AssessmentInvitationDeliveryStatus.Pending,
            expiresUtc: DateTime.UtcNow.AddHours(4),
            revokedUtc: null,
            email: "customer@example.com");

        var service = new AssessmentInvitationDeliveryService(fixture.Db, sender, config);

        var result = await service.DeliverAsync(new AssessmentInvitationDeliveryRequest(
            seeded.AssessmentId,
            seeded.InvitationId,
            "customer@example.com",
            "token",
            seeded.ExpiresUtc));

        Assert.False(result.Succeeded);
        Assert.Equal("DevelopmentEmailSuppressed", result.FailureCode);
        Assert.True(result.IsDevelopmentEmailSuppressed);

        var saved = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == seeded.InvitationId);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Pending, saved.DeliveryStatus);
        Assert.Equal("DevEmailSuppressed", saved.DeliveryFailureCode);
        Assert.Null(saved.DeliveryAcceptedUtc);
    }

    [Fact]
    public async Task DevelopmentSuppressingEmailSender_Never_Sends_And_Returns_Suppressed()
    {
        var sender = new DevelopmentSuppressingEmailSender();

        var result = await sender.SendAsync("customer@example.com", "subject", "<p>body</p>");

        Assert.Equal(EmailSendOutcome.Suppressed, result.Outcome);
    }

    [Fact]
    public void Program_Registers_SesEmailSender_For_NonDevelopment()
    {
        var programPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Program.cs"));
        Assert.True(File.Exists(programPath), $"Program.cs not found at '{programPath}'.");

        var programContent = File.ReadAllText(programPath);
        Assert.Contains("AddScoped<IEmailSender, SesEmailSender>();", programContent, StringComparison.Ordinal);
        Assert.Contains("AddScoped<IEmailSender, DevelopmentSuppressingEmailSender>();", programContent, StringComparison.Ordinal);
    }

    private static IConfiguration CreateConfig(string baseUrl)
    {
        var values = new Dictionary<string, string?>
        {
            ["AppUrls:Base"] = baseUrl
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public string? LastToEmail { get; private set; }
        public string? LastSubject { get; private set; }
        public string? LastBody { get; private set; }
        public int SendCallCount { get; private set; }

        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
        {
            SendCallCount++;
            LastToEmail = toEmail;
            LastSubject = subject;
            LastBody = htmlBody;
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

        public async Task<(Guid AssessmentId, Guid InvitationId, DateTime ExpiresUtc)> SeedInvitationAsync(
            AssessmentStatus assessmentStatus,
            AssessmentInvitationDeliveryStatus deliveryStatus,
            DateTime expiresUtc,
            DateTime? revokedUtc,
            string email)
        {
            var assessmentId = Guid.NewGuid();
            var invitationId = Guid.NewGuid();

            Db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "Assessment",
                SiteName = "Site",
                ContactEmail = email,
                Status = assessmentStatus,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            Db.Set<AssessmentInvitation>().Add(new AssessmentInvitation
            {
                Id = invitationId,
                AssessmentId = assessmentId,
                Email = email,
                TokenHash = "seeded-hash",
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = expiresUtc,
                RevokedUtc = revokedUtc,
                DeliveryStatus = deliveryStatus,
                DeliveryAttemptCount = 0
            });

            await Db.SaveChangesAsync();

            return (assessmentId, invitationId, expiresUtc);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
