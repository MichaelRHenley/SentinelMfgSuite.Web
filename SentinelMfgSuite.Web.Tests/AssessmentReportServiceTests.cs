using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class AssessmentReportServiceTests
{
    [Fact]
    public async Task CreateAsync_Creates_Report_And_Initial_Scoring_Revision()
    {
        await using var fixture = await ReportDatabase.CreateAsync();
        var caller = Admin("owner-1");
        var (assessmentId, responseId) = await fixture.SeedAsync(callerId: "owner-1");

        var result = await fixture.Service.CreateAsync(assessmentId, responseId, caller);

        Assert.Equal(AssessmentReportServiceState.Created, result.State);
        var report = Assert.IsType<AssessmentReport>(result.Report);
        Assert.Equal(responseId, report.SubmittedResponseId);
        Assert.Single(report.Revisions);
        var revision = Assert.Single(report.Revisions);
        Assert.Equal(1, revision.RevisionNumber);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, revision.Status);
        Assert.Equal(string.Empty, revision.ExecutiveSummary);
        Assert.NotNull(revision.ScoringSnapshot);
        Assert.Equal(ReadinessScoringSnapshotSerializer.PersistedSchemaVersion, revision.ScoringSnapshotSchemaVersion);
        Assert.Null(revision.ScoringContractVersion);
        Assert.Equal("sentinel-assessment-report/1", revision.ReportTemplateVersion);
        Assert.Equal("owner-1", revision.CreatedByUserId);
        var snapshot = ReadinessScoringSnapshotSerializer.Deserialize(revision.ScoringSnapshot!);
        Assert.Equal(ReadinessScoringSnapshotSerializer.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(ReadinessOverallStatus.Eligible, snapshot.Snapshot.OverallStatus);

        var history = Assert.Single(await fixture.Db.Set<AssessmentReportTransitionHistory>().AsNoTracking().ToListAsync());
        Assert.Equal(AssessmentReportTransitionEventType.Created, history.EventType);
        Assert.Equal(report.Id, history.AssessmentReportId);
        Assert.Equal(revision.Id, history.AssessmentReportRevisionId);
        Assert.Equal("owner-1", history.ActorUserId);
        Assert.Equal(revision.CreatedUtc, history.OccurredUtc);
        Assert.Null(history.PreviousStatus);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, history.NewStatus);
        Assert.Null(history.Reason);
        Assert.NotEqual(Guid.Empty, history.Id);
        Assert.False(string.IsNullOrWhiteSpace(history.CorrelationId));
    }

    [Fact]
    public async Task CreateAsync_Failure_Persists_No_Report_Revision_Or_History()
    {
        await using var fixture = await ReportDatabase.CreateAsync();
        var (assessmentId, responseId) = await fixture.SeedAsync(callerId: "owner-1");
        var service = new AssessmentReportService(fixture.Db, new ThrowingScoringService());

        await Assert.ThrowsAnyAsync<Exception>(() => service.CreateAsync(assessmentId, responseId, Admin("owner-1")));

        await using var verify = new ApplicationDbContext(fixture.Options);
        Assert.Equal(0, await verify.Set<AssessmentReport>().CountAsync());
        Assert.Equal(0, await verify.Set<AssessmentReportRevision>().CountAsync());
        Assert.Equal(0, await verify.Set<AssessmentReportTransitionHistory>().CountAsync());
    }

    [Fact]
    public void SnapshotSerializer_Preserves_Withheld_And_HistoricalNoScore_Outcomes()
    {
        var scoring = new ReadinessScoringService();
        var withheld = scoring.Score("2.0", ValidSubmission("2.0"));
        var historical = scoring.Score("1.0", ValidSubmission("1.0"));

        var withheldSnapshot = ReadinessScoringSnapshotSerializer.Deserialize(
            ReadinessScoringSnapshotSerializer.Serialize(withheld)).Snapshot;
        var historicalSnapshot = ReadinessScoringSnapshotSerializer.Deserialize(
            ReadinessScoringSnapshotSerializer.Serialize(historical)).Snapshot;

        Assert.Equal(ReadinessOverallStatus.Withheld, withheldSnapshot.OverallStatus);
        Assert.Null(withheldSnapshot.OverallRawScore0To100);
        Assert.Null(withheldSnapshot.OverallDisplayScore0To100);
        Assert.Contains(ReadinessScoreReason.OverallNotAvailableForV2, withheldSnapshot.WithholdingReasons);
        Assert.Equal(withheld.Dimensions.Count, withheldSnapshot.Dimensions.Count);
        Assert.Equal(ReadinessOverallStatus.HistoricalNoScore, historicalSnapshot.OverallStatus);
        Assert.Null(historicalSnapshot.OverallRawScore0To100);
        Assert.Contains(ReadinessScoreReason.HistoricalVersionNotScored, historicalSnapshot.WithholdingReasons);
        Assert.Equal(ReadinessItemStatus.Scoreable, withheldSnapshot.Dimensions.SelectMany(dimension => dimension.Items)
            .First(item => item.Status == ReadinessItemStatus.Scoreable).Status);
    }

    [Fact]
    public void SnapshotSerializer_Rejects_Unknown_Schema_Version()
    {
        var exception = Assert.Throws<System.Text.Json.JsonException>(
            () => ReadinessScoringSnapshotSerializer.Deserialize("{\"schemaVersion\":\"2.0\",\"snapshot\":{}}"));

        Assert.Contains("Unsupported scoring snapshot schema version", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_Rejects_Invalid_Assessment_Id()
    {
        await using var fixture = await ReportDatabase.CreateAsync();
        var (_, responseId) = await fixture.SeedAsync(callerId: "owner-1");

        var result = await fixture.Service.CreateAsync(Guid.NewGuid(), responseId, Admin("owner-1"));

        Assert.Equal(AssessmentReportServiceState.AssessmentNotFound, result.State);
    }

    [Fact]
    public async Task CreateAsync_Rejects_Invalid_Response_Id()
    {
        await using var fixture = await ReportDatabase.CreateAsync();
        var (assessmentId, _) = await fixture.SeedAsync(callerId: "owner-1");

        var result = await fixture.Service.CreateAsync(assessmentId, Guid.NewGuid(), Admin("owner-1"));

        Assert.Equal(AssessmentReportServiceState.ResponseNotFound, result.State);
    }

    [Fact]
    public async Task CreateAsync_Rejects_Response_Belonging_To_Another_Assessment()
    {
        await using var fixture = await ReportDatabase.CreateAsync();
        var (assessmentId, responseId) = await fixture.SeedAsync(callerId: "owner-1");
        var otherAssessmentId = await fixture.SeedAssessmentAsync("owner-1");

        var result = await fixture.Service.CreateAsync(otherAssessmentId, responseId, Admin("owner-1"));

        Assert.Equal(AssessmentReportServiceState.ResponseAssessmentMismatch, result.State);
        Assert.Equal(0, await fixture.Db.Set<AssessmentReport>().CountAsync());
        Assert.NotEqual(Guid.Empty, assessmentId);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task CreateAsync_Rejects_Draft_Or_Unsubmitted_Response(bool isDraft, bool hasSubmittedUtc)
    {
        await using var fixture = await ReportDatabase.CreateAsync();
        var (assessmentId, responseId) = await fixture.SeedAsync(
            callerId: "owner-1",
            isDraft: isDraft,
            hasSubmittedUtc: hasSubmittedUtc);

        var result = await fixture.Service.CreateAsync(assessmentId, responseId, Admin("owner-1"));

        Assert.Equal(AssessmentReportServiceState.ResponseNotSubmitted, result.State);
    }

    [Fact]
    public async Task CreateAsync_Rejects_Incomplete_Submitted_Payload()
    {
        await using var fixture = await ReportDatabase.CreateAsync();
        var (assessmentId, responseId) = await fixture.SeedAsync(
            callerId: "owner-1",
            payload: QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1()));

        var result = await fixture.Service.CreateAsync(assessmentId, responseId, Admin("owner-1"));

        Assert.Equal(AssessmentReportServiceState.InvalidResponse, result.State);
        Assert.Equal(0, await fixture.Db.Set<AssessmentReport>().CountAsync());
    }

    [Fact]
    public async Task CreateAsync_Is_Idempotent_And_Preserves_Historical_Revision_Content()
    {
        await using var fixture = await ReportDatabase.CreateAsync();
        var caller = Admin("owner-1");
        var (assessmentId, responseId) = await fixture.SeedAsync(callerId: "owner-1");
        var initial = await fixture.Service.CreateAsync(assessmentId, responseId, caller);
        var revision = Assert.Single(initial.Report!.Revisions);
        revision.ExecutiveSummary = "Historically approved content";
        await RevisionStatusSeed.SetAsync(fixture.Db, revision, AssessmentReportRevisionStatus.Approved);

        var retry = await fixture.Service.CreateAsync(assessmentId, responseId, caller);

        Assert.Equal(AssessmentReportServiceState.AlreadyExists, retry.State);
        Assert.Equal(initial.Report.Id, retry.Report!.Id);
        Assert.Single(retry.Report.Revisions);
        Assert.Equal(1, retry.Report.Revisions.Single().RevisionNumber);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, retry.Report.Revisions.Single().Status);
        Assert.Equal("Historically approved content", retry.Report.Revisions.Single().ExecutiveSummary);
        Assert.Equal(1, await fixture.Db.Set<AssessmentReportTransitionHistory>().CountAsync());
    }

    [Fact]
    public async Task CreateAsync_Concurrent_Requests_Create_One_Report_And_One_Initial_Revision()
    {
        await using var fixture = await ReportDatabase.CreateAsync();
        var (assessmentId, responseId) = await fixture.SeedAsync(callerId: "owner-1");
        using var gate = new Barrier(2);

        async Task<AssessmentReportServiceResult> CreateInNewContextAsync()
        {
            await using var db = new ApplicationDbContext(fixture.Options);
            var service = new AssessmentReportService(db, new BarrierScoringService(gate));
            return await service.CreateAsync(assessmentId, responseId, Admin("owner-1"));
        }

        var results = await Task.WhenAll(CreateInNewContextAsync(), CreateInNewContextAsync());

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(1, await fixture.Db.Set<AssessmentReport>().CountAsync());
        Assert.Equal(1, await fixture.Db.Set<AssessmentReportRevision>().CountAsync());
        Assert.Equal(1, await fixture.Db.Set<AssessmentReportTransitionHistory>().CountAsync());
        Assert.Contains(results, result => result.State == AssessmentReportServiceState.Created);
        Assert.Contains(results, result => result.State == AssessmentReportServiceState.AlreadyExists);
    }

    [Fact]
    public async Task GetByIdAsync_Requires_Admin_And_Matching_Assessment_Owner()
    {
        await using var fixture = await ReportDatabase.CreateAsync();
        var (assessmentId, responseId) = await fixture.SeedAsync(callerId: "owner-1");
        var created = await fixture.Service.CreateAsync(assessmentId, responseId, Admin("owner-1"));

        var notAdmin = await fixture.Service.GetByIdAsync(created.Report!.Id, User("owner-1"));
        var wrongOwner = await fixture.Service.GetByIdAsync(created.Report.Id, Admin("other-owner"));
        var ownerAdmin = await fixture.Service.GetByIdAsync(created.Report.Id, Admin("owner-1"));

        Assert.Equal(AssessmentReportServiceState.Forbidden, notAdmin.State);
        Assert.Equal(AssessmentReportServiceState.NotFound, wrongOwner.State);
        Assert.Equal(AssessmentReportServiceState.Retrieved, ownerAdmin.State);
        Assert.Single(ownerAdmin.Report!.Revisions);
    }

    [Fact]
    public async Task GetBySubmittedResponseIdAsync_Returns_Owned_Report()
    {
        await using var fixture = await ReportDatabase.CreateAsync();
        var (assessmentId, responseId) = await fixture.SeedAsync(callerId: "owner-1");
        await fixture.Service.CreateAsync(assessmentId, responseId, Admin("owner-1"));

        var result = await fixture.Service.GetBySubmittedResponseIdAsync(responseId, Admin("owner-1"));

        Assert.Equal(AssessmentReportServiceState.Retrieved, result.State);
        Assert.Equal(responseId, result.Report!.SubmittedResponseId);
    }

    private static QuestionnaireResponseV1 ValidSubmission(string schemaVersion = "3.0")
        => new()
        {
            SchemaVersion = schemaVersion,
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q01"] = new() { Composite = new Dictionary<string, FieldAnswer>
                {
                    ["facility_name"] = new() { Text = "Plant" },
                    ["facility_location"] = new() { Text = "Building A" }
                } },
                ["q04"] = new() { Selected = "11-25" },
                ["q05"] = new() { Selected = "two_shifts" },
                ["q06"] = new() { SelectedValues = new List<string> { "unplanned_downtime", "quality" } },
                ["q07"] = new() { Selected = "quality" },
                ["q11"] = new() { SelectedValues = new List<string> { "plc", "mes" } },
                ["q14"] = new() { SelectedValues = new List<string> { "auto_equipment", "db_queries" } },
                ["q15"] = new() { Composite = new Dictionary<string, FieldAnswer>
                {
                    ["integration_level"] = new() { Selected = "mostly" }
                } },
                ["q17"] = new() { Composite = new Dictionary<string, FieldAnswer>
                {
                    ["history_choice"] = new() { Selected = "most_relevant" }
                } },
                ["q19"] = new() { Selected = "yes" },
                ["q20"] = new() { Selected = "standardized" },
                ["q21"] = new() { Selected = "automatic" },
                ["q24"] = new() { Selected = "automated_validated" },
                ["q27"] = new() { Composite = new Dictionary<string, FieldAnswer>
                {
                    ["desired_outcome"] = new() { Text = "Reduce downtime" },
                    ["baseline_available"] = new() { Selected = "partial" }
                } },
                ["q31"] = new() { Selected = "consistently_accurate" },
                ["q32"] = new() { Selected = "proactive_decisions" },
                ["q33"] = new() { Selected = "nearly_all" },
                ["q34"] = new() { Selected = "consistently_reliable" },
                ["q35"] = new() { Selected = "systematic_monitoring" },
                ["q36"] = new() { Selected = "almost_never_missing" }
            }
        };

    private static ClaimsPrincipal Admin(string userId)
        => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Role, "AssessmentAdmin")
        }, "test"));

    private static ClaimsPrincipal User(string userId)
        => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "test"));

    private sealed class ThrowingScoringService : IReadinessScoringService
    {
        public ReadinessScoringResult Score(string? questionnaireVersion, QuestionnaireResponseV1? response)
            => throw new InvalidOperationException("scoring failure");
    }

    private sealed class BarrierScoringService(Barrier barrier) : IReadinessScoringService
    {
        private readonly IReadinessScoringService _inner = new ReadinessScoringService();

        public ReadinessScoringResult Score(string? questionnaireVersion, QuestionnaireResponseV1? response)
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return _inner.Score(questionnaireVersion, response);
        }
    }

    private sealed class ReportDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ApplicationDbContext Db { get; }
        public DbContextOptions<ApplicationDbContext> Options { get; }
        public AssessmentReportService Service { get; }

        private ReportDatabase(SqliteConnection connection, string connectionString, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
            Options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connectionString).Options;
            Service = new AssessmentReportService(db, new ReadinessScoringService());
        }

        public static async Task<ReportDatabase> CreateAsync()
        {
            var connectionString = $"Data Source=assessment-report-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connectionString).Options;
            var db = new ApplicationDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new ReportDatabase(connection, connectionString, db);
        }

        public async Task<(Guid AssessmentId, Guid ResponseId)> SeedAsync(
            string callerId,
            bool isDraft = false,
            bool hasSubmittedUtc = true,
            string? payload = null)
        {
            var assessmentId = await SeedAssessmentAsync(callerId);
            var invitationId = Guid.NewGuid();
            var responseId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            Db.Add(new AssessmentInvitation
            {
                Id = invitationId,
                AssessmentId = assessmentId,
                Email = "customer@example.com",
                TokenHash = $"hash-{invitationId:N}",
                CreatedUtc = now,
                ExpiresUtc = now.AddDays(1)
            });
            Db.Add(new AssessmentResponse
            {
                Id = responseId,
                AssessmentId = assessmentId,
                InvitationId = invitationId,
            Data = payload ?? QuestionnaireResponseV1Serializer.Serialize(ValidSubmission("3.0")),
                IsDraft = isDraft,
                LastSavedUtc = now,
                SubmittedUtc = hasSubmittedUtc ? now : null
            });
            await Db.SaveChangesAsync();
            return (assessmentId, responseId);
        }

        public async Task<Guid> SeedAssessmentAsync(string callerId)
        {
            var id = Guid.NewGuid();
            var now = DateTime.UtcNow;
            Db.Add(new Assessment
            {
                Id = id,
                OwnerUserId = callerId,
                Status = AssessmentStatus.Submitted,
                CreatedUtc = now,
                UpdatedUtc = now,
                QuestionnaireVersion = "3.0"
            });
            await Db.SaveChangesAsync();
            return id;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }

    }
}