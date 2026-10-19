using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentProvisioningFoundationTests
{
    [Fact]
    public void Program_Registers_AssessmentProvisioningService_As_Scoped()
    {
        var programPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Program.cs"));
        Assert.True(File.Exists(programPath), $"Program.cs not found at '{programPath}'.");

        var programContent = File.ReadAllText(programPath);
        Assert.Matches(
            new Regex(@"AddScoped\s*<\s*IAssessmentProvisioningService\s*,\s*AssessmentProvisioningService\s*>\s*\(\s*\)\s*;", RegexOptions.CultureInvariant),
            programContent);
    }

    [Fact]
    public void Request_Allows_Optional_Tenant_Owner_And_External_Purchase_Reference()
    {
        var request = new AssessmentProvisioningRequest(
            AssessmentProvisioningInitiationSource.AutomaticPurchase,
            "  Industrial Readiness Assessment  ",
            "  Plant A  ",
            "  customer@example.com  ");

        Assert.Equal(AssessmentProvisioningInitiationSource.AutomaticPurchase, request.InitiationSource);
        Assert.Equal("Industrial Readiness Assessment", request.AssessmentTitle);
        Assert.Equal("Plant A", request.SiteName);
        Assert.Equal("customer@example.com", request.CustomerContactEmail);
        Assert.Null(request.TenantId);
        Assert.Null(request.OwnerUserId);
        Assert.Null(request.ExternalPurchaseReference);
    }

    [Fact]
    public void Scoped_Resolution_Uses_Same_DbContext_For_Provisioning_And_Delivery()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddSingleton(connection);
        services.AddDbContext<ApplicationDbContext>((provider, options) =>
            options.UseSqlite(provider.GetRequiredService<SqliteConnection>()));

        services.AddScoped<IEmailSender, NoOpEmailSender>();
        services.AddScoped<IAssessmentProvisioningService, AssessmentProvisioningService>();
        services.AddScoped<IAssessmentInvitationIssuanceTransactionParticipant, AssessmentInvitationIssuanceService>();
        services.AddScoped<IAssessmentInvitationDeliveryService, AssessmentInvitationDeliveryService>();
        services.AddSingleton<IConfiguration>(CreateConfig("https://sentinelaisystems.net"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var provisioning = Assert.IsType<AssessmentProvisioningService>(
            scope.ServiceProvider.GetRequiredService<IAssessmentProvisioningService>());
        var delivery = Assert.IsType<AssessmentInvitationDeliveryService>(
            scope.ServiceProvider.GetRequiredService<IAssessmentInvitationDeliveryService>());

        var provisioningDb = Assert.IsType<ApplicationDbContext>(
            typeof(AssessmentProvisioningService)
                .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(provisioning));

        var deliveryFromProvisioning = Assert.IsType<AssessmentInvitationDeliveryService>(
            typeof(AssessmentProvisioningService)
                .GetField("_deliveryService", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(provisioning));

        var deliveryDbFromProvisioning = Assert.IsType<ApplicationDbContext>(
            typeof(AssessmentInvitationDeliveryService)
                .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(deliveryFromProvisioning));

        var deliveryDb = Assert.IsType<ApplicationDbContext>(
            typeof(AssessmentInvitationDeliveryService)
                .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(delivery));

        Assert.Same(provisioningDb, deliveryDbFromProvisioning);
        Assert.Same(provisioningDb, deliveryDb);
    }

    [Theory]
    [InlineData("", "Site", "customer@example.com")]
    [InlineData("Assessment", "", "customer@example.com")]
    [InlineData("Assessment", "Site", "")]
    public void Request_Throws_For_Required_Blank_Values(string title, string site, string email)
    {
        Assert.Throws<ArgumentException>(() => new AssessmentProvisioningRequest(
            AssessmentProvisioningInitiationSource.ManualAdmin,
            title,
            site,
            email));
    }

    [Fact]
    public void Result_Does_Not_Expose_Invitation_Token_Data()
    {
        var tokenProperty = typeof(AssessmentProvisioningResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));

        Assert.False(tokenProperty);
    }

    [Fact]
    public async Task ProvisionAsync_For_ManualAdmin_Creates_Assessment_Issues_Invitation_And_Sends_Email_After_Commit()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var sender = new CommitAwareEmailSender(fixture.Db);
        var deliveryService = new AssessmentInvitationDeliveryService(fixture.Db, sender, CreateConfig("https://sentinelaisystems.net"));
        var issuanceParticipant = new AssessmentInvitationIssuanceService(fixture.Db);
        var service = new AssessmentProvisioningService(fixture.Db, deliveryService, issuanceParticipant);

        var request = new AssessmentProvisioningRequest(
            AssessmentProvisioningInitiationSource.ManualAdmin,
            "Industrial Data & AI Readiness Assessment",
            "North Plant",
            "customer@example.com",
            ownerUserId: "admin-user-1");

        var result = await service.ProvisionAsync(request);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.AssessmentId);
        Assert.NotNull(result.InvitationId);
        Assert.Null(result.FailureCode);
        Assert.Equal(1, sender.SendCallCount);
        Assert.True(sender.SawCommittedState);
        Assert.False(string.IsNullOrWhiteSpace(sender.LastBody));

        var token = ExtractInvitationToken(sender.LastBody!);
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.DoesNotContain(token, result.ToString(), StringComparison.Ordinal);

        var savedAssessment = await fixture.Db.Set<Assessment>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == result.AssessmentId!.Value);
        var savedInvitation = await fixture.Db.Set<AssessmentInvitation>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == result.InvitationId!.Value);

        Assert.Equal(savedAssessment.Id, result.AssessmentId);
        Assert.Equal(savedInvitation.Id, result.InvitationId);
        Assert.Equal("Industrial Data & AI Readiness Assessment", savedAssessment.Title);
        Assert.Equal("North Plant", savedAssessment.SiteName);
        Assert.Equal("customer@example.com", savedAssessment.ContactEmail);
        Assert.Equal("admin-user-1", savedAssessment.OwnerUserId);
        Assert.Equal(AssessmentStatus.Invited, savedAssessment.Status);
        Assert.Equal("2.0", savedAssessment.QuestionnaireVersion);
        Assert.Equal(savedAssessment.Id, savedInvitation.AssessmentId);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Accepted, savedInvitation.DeliveryStatus);
        Assert.NotEqual(Guid.Empty, savedAssessment.Id);
        Assert.NotEqual(default, savedAssessment.CreatedUtc);
        Assert.NotEqual(default, savedAssessment.UpdatedUtc);
    }

    [Fact]
    public async Task ProvisionManualWithInvitationAtomicAsync_Creates_Assessment_And_Invitation_Atomically()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var participant = new AssessmentInvitationIssuanceService(fixture.Db);
        var service = new AssessmentProvisioningService(fixture.Db, participant);

        var request = new AssessmentProvisioningRequest(
            AssessmentProvisioningInitiationSource.ManualAdmin,
            "Atomic Assessment",
            "North Plant",
            "customer@example.com",
            ownerUserId: "admin-user-1");

        var result = await service.ProvisionManualWithInvitationAtomicAsync(request, TimeSpan.FromHours(12));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.AssessmentId);
        Assert.NotNull(result.InvitationId);
        Assert.False(string.IsNullOrWhiteSpace(result.InvitationToken));

        var savedAssessment = await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == result.AssessmentId!.Value);
        var savedInvitation = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == result.InvitationId!.Value);

        Assert.Equal(AssessmentStatus.Invited, savedAssessment.Status);
        Assert.Equal(savedAssessment.Id, savedInvitation.AssessmentId);
        Assert.Null(savedInvitation.RevokedUtc);
    }

    [Fact]
    public async Task ProvisionAsync_For_ManualAdmin_Preserves_Assessment_And_Invitation_When_Email_Delivery_Fails()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var sender = new ThrowingEmailSender();
        var deliveryService = new AssessmentInvitationDeliveryService(fixture.Db, sender, CreateConfig("https://sentinelaisystems.net"));
        var issuanceParticipant = new AssessmentInvitationIssuanceService(fixture.Db);
        var service = new AssessmentProvisioningService(fixture.Db, deliveryService, issuanceParticipant);

        var request = new AssessmentProvisioningRequest(
            AssessmentProvisioningInitiationSource.ManualAdmin,
            "Atomic Assessment",
            "North Plant",
            "customer@example.com");

        var result = await service.ProvisionAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("EmailDeliveryFailed", result.FailureCode);
        Assert.NotNull(result.AssessmentId);
        Assert.NotNull(result.InvitationId);

        var savedAssessment = await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == result.AssessmentId!.Value);
        var savedInvitation = await fixture.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(x => x.Id == result.InvitationId!.Value);

        Assert.Equal(AssessmentStatus.Invited, savedAssessment.Status);
        Assert.Equal(savedAssessment.Id, savedInvitation.AssessmentId);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Failed, savedInvitation.DeliveryStatus);
        Assert.Equal(1, savedInvitation.DeliveryAttemptCount);
        Assert.Equal("EmailProviderFailure", savedInvitation.DeliveryFailureCode);
    }

    [Fact]
    public async Task ProvisionAsync_For_ManualAdmin_Does_Not_Send_Email_When_Provisioning_Fails()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var sender = new CommitAwareEmailSender(fixture.Db);
        var deliveryService = new AssessmentInvitationDeliveryService(fixture.Db, sender, CreateConfig("https://sentinelaisystems.net"));
        var participant = new FailingIssuanceParticipant("DuplicateActiveInvitation", "Simulated issuance failure");
        var service = new AssessmentProvisioningService(fixture.Db, deliveryService, participant);

        var request = new AssessmentProvisioningRequest(
            AssessmentProvisioningInitiationSource.ManualAdmin,
            "Atomic Assessment",
            "North Plant",
            "customer@example.com");

        var result = await service.ProvisionAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("DuplicateActiveInvitation", result.FailureCode);
        Assert.Null(result.AssessmentId);
        Assert.Null(result.InvitationId);
        Assert.Equal(0, sender.SendCallCount);

        var assessmentCount = await fixture.Db.Set<Assessment>().CountAsync();
        var invitationCount = await fixture.Db.Set<AssessmentInvitation>().CountAsync();
        Assert.Equal(0, assessmentCount);
        Assert.Equal(0, invitationCount);
    }

    [Fact]
    public async Task ProvisionManualWithInvitationAtomicAsync_Uses_Existing_DbContext_Transaction_For_Issuance()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var participant = new AssertingIssuanceParticipant(fixture.Db);
        var service = new AssessmentProvisioningService(fixture.Db, participant);

        var request = new AssessmentProvisioningRequest(
            AssessmentProvisioningInitiationSource.ManualAdmin,
            "Atomic Assessment",
            "North Plant",
            "customer@example.com");

        var result = await service.ProvisionManualWithInvitationAtomicAsync(request, TimeSpan.FromHours(12));

        Assert.True(result.Succeeded);
        Assert.True(participant.SawActiveTransaction);
    }

    [Fact]
    public async Task ProvisionAsync_For_InternalDraftAdmin_Creates_Draft_Only_Without_Invitation_Or_Delivery()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var delivery = new TrackingDeliveryService();
        var issuance = new GuardIssuanceParticipant();
        var service = new AssessmentProvisioningService(fixture.Db, delivery, issuance);

        var request = new AssessmentProvisioningRequest(
            AssessmentProvisioningInitiationSource.InternalDraftAdmin,
            "Draft Intake",
            "Assembly Plant",
            "customer@example.com",
            ownerUserId: "admin-user-1");

        var result = await service.ProvisionAsync(request);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.AssessmentId);
        Assert.Null(result.InvitationId);
        Assert.Equal(0, delivery.CallCount);
        Assert.Equal(0, issuance.CallCount);

        var assessment = await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == result.AssessmentId!.Value);
        Assert.Equal(AssessmentStatus.Draft, assessment.Status);
        Assert.Equal("admin-user-1", assessment.OwnerUserId);

        var invitationCount = await fixture.Db.Set<AssessmentInvitation>().CountAsync();
        Assert.Equal(0, invitationCount);

        var responseCount = await fixture.Db.Set<AssessmentResponse>().CountAsync();
        Assert.Equal(0, responseCount);

        var tenantCount = await fixture.Db.Set<SentinelMfgSuite.Web.Models.Tenants.Tenant>().CountAsync();
        Assert.Equal(0, tenantCount);

        var userCount = await fixture.Db.Users.CountAsync();
        Assert.Equal(0, userCount);
    }

    [Fact]
    public async Task ProvisionAsync_For_AutomaticPurchase_Without_ExternalReference_Fails_And_DoesNotPersist()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var service = new AssessmentProvisioningService(fixture.Db);

        var request = new AssessmentProvisioningRequest(
            AssessmentProvisioningInitiationSource.AutomaticPurchase,
            "Industrial Data & AI Readiness Assessment",
            "North Plant",
            "customer@example.com");

        var result = await service.ProvisionAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("ExternalPurchaseReferenceRequired", result.FailureCode);
        Assert.Null(result.AssessmentId);
        Assert.Null(result.InvitationId);

        var count = await fixture.Db.Set<Assessment>().CountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ProvisionAsync_For_AutomaticPurchase_With_ExternalReference_Fails_As_NotReady_And_DoesNotPersist()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var service = new AssessmentProvisioningService(fixture.Db);

        var request = new AssessmentProvisioningRequest(
            AssessmentProvisioningInitiationSource.AutomaticPurchase,
            "Industrial Data & AI Readiness Assessment",
            "North Plant",
            "customer@example.com",
            externalPurchaseReference: "order-123");

        var result = await service.ProvisionAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("AutomaticPurchaseNotReady", result.FailureCode);
        Assert.Null(result.AssessmentId);
        Assert.Null(result.InvitationId);

        var count = await fixture.Db.Set<Assessment>().CountAsync();
        Assert.Equal(0, count);
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

    private sealed class NoOpEmailSender : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
            => Task.FromResult(EmailSendResult.Accepted());
    }

    private sealed class TrackingDeliveryService : IAssessmentInvitationDeliveryService
    {
        public int CallCount { get; private set; }

        public Task<AssessmentInvitationDeliveryResult> DeliverAsync(AssessmentInvitationDeliveryRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(AssessmentInvitationDeliveryResult.Success());
        }
    }

    private sealed class GuardIssuanceParticipant : IAssessmentInvitationIssuanceTransactionParticipant
    {
        public int CallCount { get; private set; }

        public Task<AssessmentInvitationIssuanceResult> IssueWithinExistingTransactionAsync(Guid assessmentId, TimeSpan expiresIn, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(AssessmentInvitationIssuanceResult.Failure("ShouldNotBeCalled", "Issuance should not be called for draft-only provisioning."));
        }
    }

    private sealed class FailingIssuanceParticipant : IAssessmentInvitationIssuanceTransactionParticipant
    {
        private readonly string _failureCode;
        private readonly string _failureMessage;

        public FailingIssuanceParticipant(string failureCode, string failureMessage)
        {
            _failureCode = failureCode;
            _failureMessage = failureMessage;
        }

        public Task<AssessmentInvitationIssuanceResult> IssueWithinExistingTransactionAsync(Guid assessmentId, TimeSpan expiresIn, CancellationToken ct = default)
            => Task.FromResult(AssessmentInvitationIssuanceResult.Failure(_failureCode, _failureMessage));
    }

    private sealed class AssertingIssuanceParticipant : IAssessmentInvitationIssuanceTransactionParticipant
    {
        private readonly ApplicationDbContext _db;

        public bool SawActiveTransaction { get; private set; }

        public AssertingIssuanceParticipant(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<AssessmentInvitationIssuanceResult> IssueWithinExistingTransactionAsync(Guid assessmentId, TimeSpan expiresIn, CancellationToken ct = default)
        {
            SawActiveTransaction = _db.Database.CurrentTransaction is not null;

            var service = new AssessmentInvitationIssuanceService(_db);
            return await ((IAssessmentInvitationIssuanceTransactionParticipant)service)
                .IssueWithinExistingTransactionAsync(assessmentId, expiresIn, ct);
        }
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

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
