using System.Text.Json;
using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class AssessmentReportRevisionContentServiceTests
{
    [Fact]
    public async Task SaveAndGet_RoundTrip_Content_And_Leave_Response_And_Snapshot_Unchanged()
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: true);
        var originalResponse = fixture.Response.Data;
        var originalSnapshot = fixture.Revision.ScoringSnapshot;
        var content = ConsultantReportContent.CreateEmpty();

        var saved = await fixture.Service.SaveDraftAsync(fixture.Revision.Id, fixture.Revision.ConcurrencyStamp, content, AdminCaller());
        var retrieved = await fixture.Service.GetAsync(fixture.Revision.Id, AdminCaller());

        Assert.Equal(AssessmentReportRevisionContentState.Saved, saved.State);
        Assert.Equal(ConsultantReportContent.CurrentSchemaVersion, fixture.Revision.ConsultantContentSchemaVersion);
        Assert.Equal(content, retrieved.Content);
        Assert.Equal(originalResponse, fixture.Response.Data);
        Assert.Equal(originalSnapshot, fixture.Revision.ScoringSnapshot);
        Assert.Equal(ReadinessOverallStatus.Eligible, ReadinessScoringSnapshotSerializer.Deserialize(fixture.Revision.ScoringSnapshot!).Snapshot.OverallStatus);
    }

    [Fact]
    public async Task Get_Rejects_Unknown_Persisted_Content_Version()
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: true);
        fixture.Revision.ConsultantContentJson = ConsultantReportContentSerializer.Serialize(ConsultantReportContent.CreateEmpty());
        fixture.Revision.ConsultantContentSchemaVersion = "2.0";
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<JsonException>(() => fixture.Service.GetAsync(fixture.Revision.Id, AdminCaller()));
    }

    [Fact]
    public async Task SaveDraft_Rejects_Unsupported_Content_Schema_Version()
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: true);
        var unsupported = ConsultantReportContent.CreateEmpty() with { SchemaVersion = "2.0" };

        var result = await fixture.Service.SaveDraftAsync(fixture.Revision.Id, fixture.Revision.ConcurrencyStamp, unsupported, AdminCaller());

        Assert.Equal(AssessmentReportRevisionContentState.UnsupportedContentSchemaVersion, result.State);
        Assert.Null(fixture.Revision.ConsultantContentJson);
    }

    [Fact]
    public async Task Get_And_Save_Reject_Caller_Without_Assessment_Admin_Role()
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: true);
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "author-1") }, "test"));

        var retrieved = await fixture.Service.GetAsync(fixture.Revision.Id, caller);
        var saved = await fixture.Service.SaveDraftAsync(fixture.Revision.Id, fixture.Revision.ConcurrencyStamp,
            ConsultantReportContent.CreateEmpty(), caller);

        Assert.Equal(AssessmentReportRevisionContentState.Forbidden, retrieved.State);
        Assert.Equal(AssessmentReportRevisionContentState.Forbidden, saved.State);
        Assert.Null(fixture.Revision.ConsultantContentJson);
    }

    [Theory]
    [InlineData(AssessmentReportRevisionStatus.InReview)]
    [InlineData(AssessmentReportRevisionStatus.Approved)]
    [InlineData(AssessmentReportRevisionStatus.Issued)]
    [InlineData(AssessmentReportRevisionStatus.Superseded)]
    public async Task SaveDraft_Rejects_NonDraft_Revisions(AssessmentReportRevisionStatus status)
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: true);
        fixture.Revision.Status = status;
        await fixture.Db.SaveChangesAsync();
        var original = fixture.Revision.ConsultantContentJson;

        var result = await fixture.Service.SaveDraftAsync(fixture.Revision.Id, fixture.Revision.ConcurrencyStamp,
            ConsultantReportContent.CreateEmpty(), AdminCaller());

        Assert.Equal(AssessmentReportRevisionContentState.NotDraft, result.State);
        Assert.Equal(original, fixture.Revision.ConsultantContentJson);
    }

    [Fact]
    public async Task SaveDraft_Rejects_Stale_Concurrency_Token()
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: true);

        var result = await fixture.Service.SaveDraftAsync(fixture.Revision.Id, Guid.NewGuid(),
            ConsultantReportContent.CreateEmpty(), AdminCaller());

        Assert.Equal(AssessmentReportRevisionContentState.ConcurrencyConflict, result.State);
        Assert.Null(fixture.Revision.ConsultantContentJson);
    }

    [Fact]
    public async Task SaveDraft_Uses_Authoritative_Response_And_Questionnaire_Definition_For_Evidence()
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: true);
        var content = WithFinding(new ReportEvidenceReference(ReportEvidenceKind.QuestionnaireAnswer, "q01", QuestionId: "q01"));

        var valid = await fixture.Service.SaveDraftAsync(fixture.Revision.Id, fixture.Revision.ConcurrencyStamp, content, AdminCaller());
        Assert.Equal(AssessmentReportRevisionContentState.Saved, valid.State);

        fixture.Revision.ConsultantContentJson = null;
        fixture.Revision.ConsultantContentSchemaVersion = null;
        fixture.Revision.ConcurrencyStamp = Guid.NewGuid();
        await fixture.Db.SaveChangesAsync();
        var invalidContent = WithFinding(new ReportEvidenceReference(ReportEvidenceKind.QuestionnaireAnswer, "q-not-real", QuestionId: "q-not-real"));
        var invalid = await fixture.Service.SaveDraftAsync(fixture.Revision.Id, fixture.Revision.ConcurrencyStamp, invalidContent, AdminCaller());

        Assert.Equal(AssessmentReportRevisionContentState.ValidationFailed, invalid.State);
        Assert.Contains(invalid.ValidationIssues!, issue => issue.Code == "evidence-reference-invalid");
    }

    [Fact]
    public async Task SaveDraft_Preserves_Insufficient_Evidence_Snapshot_With_Null_Scores()
    {
        await using var fixture = await Fixture.CreateAsync("2.0", validSubmission: true);
        var snapshot = ReadinessScoringSnapshotSerializer.Deserialize(fixture.Revision.ScoringSnapshot!).Snapshot;

        var result = await fixture.Service.SaveDraftAsync(fixture.Revision.Id, fixture.Revision.ConcurrencyStamp,
            ConsultantReportContent.CreateEmpty(), AdminCaller());

        Assert.Equal(AssessmentReportRevisionContentState.Saved, result.State);
        Assert.Equal(ReadinessOverallStatus.Withheld, snapshot.OverallStatus);
        Assert.Null(snapshot.OverallRawScore0To100);
        Assert.Null(snapshot.OverallDisplayScore0To100);
        Assert.Null(snapshot.OverallClassification);
        Assert.Contains(ReadinessScoreReason.OverallNotAvailableForV2, snapshot.WithholdingReasons);
    }

    [Fact]
    public async Task SaveDraft_Allows_Incomplete_Executive_Summary_And_Returns_Validation_Warnings()
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: true);
        var incomplete = ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(ReportAssessmentState.Assessed)
        };

        var result = await fixture.Service.SaveDraftAsync(fixture.Revision.Id, fixture.Revision.ConcurrencyStamp, incomplete, AdminCaller());

        Assert.Equal(AssessmentReportRevisionContentState.Saved, result.State);
        Assert.Equal(incomplete, result.Content);
        Assert.NotNull(result.UpdatedUtc);
        Assert.Empty(result.ValidationIssues ?? Array.Empty<ReportContentValidationIssue>());
    }

    [Fact]
    public async Task SaveDraft_Rejects_Structural_Contradictions()
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: true);
        var contradictory = ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(Narrative: "content while not assessed")
        };

        var result = await fixture.Service.SaveDraftAsync(fixture.Revision.Id, fixture.Revision.ConcurrencyStamp, contradictory, AdminCaller());

        Assert.Equal(AssessmentReportRevisionContentState.ValidationFailed, result.State);
        Assert.Contains(result.ValidationIssues!, issue => issue.Code == "section-state-content-conflict");
    }

    [Fact]
    public void Model_Maps_Only_Content_Fields_As_Jsonb_And_Version_Length_20()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        using var db = new ApplicationDbContext(options);
        var revision = db.Model.FindEntityType(typeof(AssessmentReportRevision))!;

        Assert.Equal("jsonb", revision.FindProperty(nameof(AssessmentReportRevision.ConsultantContentJson))!.GetColumnType());
        Assert.True(revision.FindProperty(nameof(AssessmentReportRevision.ConsultantContentJson))!.IsNullable);
        Assert.Equal(20, revision.FindProperty(nameof(AssessmentReportRevision.ConsultantContentSchemaVersion))!.GetMaxLength());
        Assert.True(revision.FindProperty(nameof(AssessmentReportRevision.ConsultantContentSchemaVersion))!.IsNullable);
    }

    private static ConsultantReportContent WithFinding(ReportEvidenceReference evidence)
        => ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(
                State: ReportAssessmentState.Assessed,
                Findings: new[]
                {
                    new ReportFinding("finding-1", "Questionnaire evidence", "Questionnaire-grounded draft statement",
                        ReportFindingClassification.PreliminaryFinding, ReportContentProvenance.QuestionnaireDerived,
                        ReportVerificationStatus.SelfReported, new[] { evidence })
                })
        };

    [Fact]
    public void Access_TryGetCaller_Requires_Authentication_Actor_Id_And_AssessmentAdmin_Role()
    {
        Assert.True(AssessmentReportAccess.TryGetCaller(AdminCaller(), out var id));
        Assert.Equal("author-1", id);

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.False(AssessmentReportAccess.TryGetCaller(anonymous, out _));

        var noRole = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "author-1") }, "test"));
        Assert.False(AssessmentReportAccess.TryGetCaller(noRole, out _));

        var noActor = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "AssessmentAdmin") }, "test"));
        Assert.False(AssessmentReportAccess.TryGetCaller(noActor, out _));

        var blankActor = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "  "), new Claim(ClaimTypes.Role, "AssessmentAdmin")
        }, "test"));
        Assert.False(AssessmentReportAccess.TryGetCaller(blankActor, out _));

        var wrongRole = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "author-1"), new Claim(ClaimTypes.Role, "Other")
        }, "test"));
        Assert.False(AssessmentReportAccess.TryGetCaller(wrongRole, out _));
    }

    [Fact]
    public async Task Access_LoadOwnedRevision_Handles_Missing_Mismatched_Null_And_Matching_Owner()
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: true);

        Assert.Null(await AssessmentReportAccess.LoadOwnedRevisionAsync(fixture.Db, Guid.NewGuid(), "author-1", false, default));
        Assert.Null(await AssessmentReportAccess.LoadOwnedRevisionAsync(fixture.Db, fixture.Revision.Id, "someone-else", false, default));

        var owned = await AssessmentReportAccess.LoadOwnedRevisionAsync(fixture.Db, fixture.Revision.Id, "author-1", false, default);
        Assert.NotNull(owned);
        Assert.NotNull(owned!.AssessmentReport);
        Assert.NotNull(owned.AssessmentReport.Assessment);

        var assessment = await fixture.Db.Set<Assessment>().SingleAsync();
        assessment.OwnerUserId = null;
        await fixture.Db.SaveChangesAsync();
        Assert.NotNull(await AssessmentReportAccess.LoadOwnedRevisionAsync(fixture.Db, fixture.Revision.Id, "anyone", false, default));
    }

    [Fact]
    public async Task Get_And_Save_Return_NotFound_For_Missing_Revision_And_Ownership_Mismatch()
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: true);
        var other = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "intruder"), new Claim(ClaimTypes.Role, "AssessmentAdmin")
        }, "test"));

        Assert.Equal(AssessmentReportRevisionContentState.NotFound, (await fixture.Service.GetAsync(Guid.NewGuid(), AdminCaller())).State);
        Assert.Equal(AssessmentReportRevisionContentState.NotFound, (await fixture.Service.GetAsync(fixture.Revision.Id, other)).State);
        var saved = await fixture.Service.SaveDraftAsync(fixture.Revision.Id, fixture.Revision.ConcurrencyStamp, ConsultantReportContent.CreateEmpty(), other);
        Assert.Equal(AssessmentReportRevisionContentState.NotFound, saved.State);
        Assert.Null(fixture.Revision.ConsultantContentJson);
    }

    [Fact]
    public async Task ValidationContextBuilder_Builds_Context_Without_Mutating_Revision_Or_Snapshot()
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: true);
        var snapshotBefore = fixture.Revision.ScoringSnapshot;
        var contentBefore = fixture.Revision.ConsultantContentJson;
        var stampBefore = fixture.Revision.ConcurrencyStamp;
        var updatedBefore = fixture.Revision.UpdatedUtc;

        var context = await AssessmentReportValidationContextBuilder.BuildAsync(
            fixture.Db, fixture.Revision, AssessmentReportContentStage.Draft, default);

        Assert.NotNull(context);
        Assert.Equal(AssessmentReportContentStage.Draft, context!.Stage);
        Assert.NotNull(context.QuestionnaireDefinition);
        Assert.NotNull(context.QuestionnaireResponse);
        Assert.Equal("3.0", context.ScoringSnapshot!.QuestionnaireVersion);
        Assert.Equal(snapshotBefore, fixture.Revision.ScoringSnapshot);
        Assert.Equal(contentBefore, fixture.Revision.ConsultantContentJson);
        Assert.Equal(stampBefore, fixture.Revision.ConcurrencyStamp);
        Assert.Equal(updatedBefore, fixture.Revision.UpdatedUtc);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ValidationContextBuilder_Returns_Null_For_Missing_Or_Inconsistent_Source_Data()
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: true);
        var stage = AssessmentReportContentStage.Draft;

        var originalSnapshot = fixture.Revision.ScoringSnapshot;
        fixture.Revision.ScoringSnapshot = null;
        Assert.Null(await AssessmentReportValidationContextBuilder.BuildAsync(fixture.Db, fixture.Revision, stage, default));
        fixture.Revision.ScoringSnapshot = originalSnapshot;

        var originalSchema = fixture.Revision.ScoringSnapshotSchemaVersion;
        fixture.Revision.ScoringSnapshotSchemaVersion = 0;
        Assert.Null(await AssessmentReportValidationContextBuilder.BuildAsync(fixture.Db, fixture.Revision, stage, default));
        fixture.Revision.ScoringSnapshotSchemaVersion = originalSchema;

        var originalVersion = fixture.Report.QuestionnaireVersion;
        fixture.Report.QuestionnaireVersion = "2.0";
        Assert.Null(await AssessmentReportValidationContextBuilder.BuildAsync(fixture.Db, fixture.Revision, stage, default));
        fixture.Report.QuestionnaireVersion = originalVersion;

        fixture.Report.QuestionnaireVersion = "unknown";
        Assert.Null(await AssessmentReportValidationContextBuilder.BuildAsync(fixture.Db, fixture.Revision, stage, default));
        fixture.Report.QuestionnaireVersion = originalVersion;

        fixture.Response.IsDraft = true;
        await fixture.Db.SaveChangesAsync();
        Assert.Null(await AssessmentReportValidationContextBuilder.BuildAsync(fixture.Db, fixture.Revision, stage, default));
        fixture.Response.IsDraft = false;
        await fixture.Db.SaveChangesAsync();

        fixture.Response.Data = "{ not json";
        await fixture.Db.SaveChangesAsync();
        Assert.Null(await AssessmentReportValidationContextBuilder.BuildAsync(fixture.Db, fixture.Revision, stage, default));
    }

    [Fact]
    public async Task ValidationContextBuilder_Returns_Null_When_Source_Response_Is_Incomplete()
    {
        await using var fixture = await Fixture.CreateAsync("3.0", validSubmission: false);

        Assert.Null(await AssessmentReportValidationContextBuilder.BuildAsync(
            fixture.Db, fixture.Revision, AssessmentReportContentStage.Draft, default));
    }

    private static ClaimsPrincipal AdminCaller()
        => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "author-1"),
            new Claim(ClaimTypes.Role, "AssessmentAdmin")
        }, "test"));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ApplicationDbContext Db { get; }
        public AssessmentReportRevisionContentService Service { get; }
        public AssessmentResponse Response { get; }
        public AssessmentReportRevision Revision { get; }
        public AssessmentReport Report { get; }

        private Fixture(SqliteConnection connection, ApplicationDbContext db, AssessmentResponse response, AssessmentReport report, AssessmentReportRevision revision)
        {
            _connection = connection;
            Db = db;
            Response = response;
            Report = report;
            Revision = revision;
            Service = new AssessmentReportRevisionContentService(db, new AssessmentReportContentValidator());
        }

        public static async Task<Fixture> CreateAsync(string version, bool validSubmission)
        {
            var connectionString = $"Data Source=report-content-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connectionString).Options;
            var db = new ApplicationDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTime.UtcNow;
            var assessmentId = Guid.NewGuid();
            var responseId = Guid.NewGuid();
            var invitationId = Guid.NewGuid();
            var reportId = Guid.NewGuid();
            var response = new AssessmentResponse
            {
                Id = responseId,
                AssessmentId = assessmentId,
                InvitationId = invitationId,
                Data = QuestionnaireResponseV1Serializer.Serialize(validSubmission ? BuildSubmission(version) : new QuestionnaireResponseV1 { SchemaVersion = version }),
                IsDraft = false,
                LastSavedUtc = now,
                SubmittedUtc = now
            };
            var assessment = new Assessment
            {
                Id = assessmentId,
                OwnerUserId = "author-1",
                Status = AssessmentStatus.Submitted,
                CreatedUtc = now,
                UpdatedUtc = now,
                QuestionnaireVersion = version
            };
            var snapshotResult = new ReadinessScoringService().Score(version, BuildSubmission(version));
            var snapshotJson = ReadinessScoringSnapshotSerializer.Serialize(snapshotResult);
            var revision = new AssessmentReportRevision
            {
                Id = Guid.NewGuid(),
                AssessmentReportId = reportId,
                RevisionNumber = 1,
                Status = AssessmentReportRevisionStatus.Draft,
                ScoringSnapshotSchemaVersion = ReadinessScoringSnapshotSerializer.PersistedSchemaVersion,
                ScoringSnapshot = snapshotJson,
                CreatedUtc = now,
                UpdatedUtc = now,
                CreatedByUserId = "author-1",
                UpdatedByUserId = "author-1"
            };
            var report = new AssessmentReport
            {
                Id = reportId,
                AssessmentId = assessmentId,
                SubmittedResponseId = responseId,
                QuestionnaireVersion = version,
                ResponseSubmittedUtc = now,
                CreatedUtc = now,
                UpdatedUtc = now,
                CreatedByUserId = "author-1",
                UpdatedByUserId = "author-1",
                Revisions = new List<AssessmentReportRevision> { revision }
            };
            db.Add(assessment);
            db.Add(new AssessmentInvitation
            {
                Id = invitationId,
                AssessmentId = assessmentId,
                Email = "customer@example.com",
                TokenHash = "fixture-hash",
                CreatedUtc = now,
                ExpiresUtc = now.AddDays(1)
            });
            db.Add(response);
            db.Add(report);
            await db.SaveChangesAsync();
            return new Fixture(connection, db, response, report, revision);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private static QuestionnaireResponseV1 BuildSubmission(string version)
    {
        var response = new QuestionnaireResponseV1
        {
            SchemaVersion = version,
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
                ["q15"] = new() { Composite = new Dictionary<string, FieldAnswer> { ["integration_level"] = new() { Selected = "mostly" } } },
                ["q17"] = new() { Composite = new Dictionary<string, FieldAnswer> { ["history_choice"] = new() { Selected = "most_relevant" } } },
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
        var validIds = QuestionnaireDefinitionResolver.Resolve(version).Sections
            .SelectMany(section => section.Questions).Select(question => question.Id).ToHashSet(StringComparer.Ordinal);
        response.Answers = response.Answers.Where(entry => validIds.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        return response;
    }
}
