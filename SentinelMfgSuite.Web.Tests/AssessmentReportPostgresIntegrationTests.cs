using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class AssessmentReportPostgresIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ReportService_Persists_Idempotently_And_Concurrent_Creation_Has_No_Duplicates()
    {
        var fixture = CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var userId = $"report-integration-{Guid.NewGuid():N}";
        var parallelAssessmentId = Guid.Empty;
        var parallelInvitationId = Guid.Empty;
        var parallelResponseId = Guid.Empty;

        try
        {
            await SeedAsync(fixture, assessmentId, invitationId, responseId, userId, false);
            var caller = Admin(userId);

            var first = await CreateAsync(fixture, assessmentId, responseId, caller);
            Assert.Equal(AssessmentReportServiceState.Created, first.State);
            var revisionId = Assert.Single(first.Report!.Revisions).Id;
            var originalSnapshot = first.Report.Revisions.Single().ScoringSnapshot;
            Assert.Equal(ReadinessScoringSnapshotSerializer.PersistedSchemaVersion, first.Report.Revisions.Single().ScoringSnapshotSchemaVersion);

            var retry = await CreateAsync(fixture, assessmentId, responseId, caller);
            Assert.Equal(AssessmentReportServiceState.AlreadyExists, retry.State);
            Assert.Equal(first.Report.Id, retry.Report!.Id);
            Assert.Equal(revisionId, Assert.Single(retry.Report.Revisions).Id);
            AssertSnapshotEquivalent(originalSnapshot!, retry.Report.Revisions.Single().ScoringSnapshot!);

            await using (var updateDb = fixture.CreateDbContext())
            {
                var revision = await updateDb.Set<AssessmentReportRevision>().SingleAsync(item => item.Id == revisionId);
                revision.ExecutiveSummary = "Historical content to preserve";
                revision.Status = AssessmentReportRevisionStatus.Approved;
                await updateDb.SaveChangesAsync();
            }

            parallelAssessmentId = Guid.NewGuid();
            parallelInvitationId = Guid.NewGuid();
            parallelResponseId = Guid.NewGuid();
            await SeedAsync(fixture, parallelAssessmentId, parallelInvitationId, parallelResponseId, userId, false);
            using var gate = new Barrier(2);
            var concurrentResults = await Task.WhenAll(
                CreateAsync(fixture, parallelAssessmentId, parallelResponseId, caller, gate),
                CreateAsync(fixture, parallelAssessmentId, parallelResponseId, caller, gate));

            Assert.All(concurrentResults, result => Assert.True(result.Succeeded));
            Assert.Single(concurrentResults.Where(result => result.State == AssessmentReportServiceState.Created));
            Assert.Single(concurrentResults.Where(result => result.State == AssessmentReportServiceState.AlreadyExists));

            await using var verifyDb = fixture.CreateDbContext();
            Assert.Equal(1, await verifyDb.Set<AssessmentReport>().CountAsync(item => item.SubmittedResponseId == responseId));
            Assert.Equal(1, await verifyDb.Set<AssessmentReportRevision>().CountAsync(item => item.AssessmentReportId == first.Report.Id));
            Assert.Equal(1, await verifyDb.Set<AssessmentReport>().CountAsync(item => item.SubmittedResponseId == parallelResponseId));
            Assert.Equal(1, await verifyDb.Set<AssessmentReportRevision>().CountAsync(item => item.AssessmentReportId == concurrentResults[0].Report!.Id));

            var preserved = await CreateAsync(fixture, assessmentId, responseId, caller);
            Assert.Equal(AssessmentReportRevisionStatus.Approved, preserved.Report!.Revisions.Single().Status);
            Assert.Equal("Historical content to preserve", preserved.Report.Revisions.Single().ExecutiveSummary);
            AssertSnapshotEquivalent(originalSnapshot!, preserved.Report.Revisions.Single().ScoringSnapshot!);
        }
        finally
        {
            await CleanupAsync(fixture, assessmentId, invitationId, responseId);
            if (parallelAssessmentId != Guid.Empty)
            {
                await CleanupAsync(fixture, parallelAssessmentId, parallelInvitationId, parallelResponseId);
            }
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ReportService_Rejects_Unsubmitted_Response_And_Database_Enforces_Foreign_Key_And_Revision_Uniqueness()
    {
        var fixture = CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var userId = $"report-integration-{Guid.NewGuid():N}";

        try
        {
            await SeedAsync(fixture, assessmentId, invitationId, responseId, userId, true);
            var result = await CreateAsync(fixture, assessmentId, responseId, Admin(userId));
            Assert.Equal(AssessmentReportServiceState.ResponseNotSubmitted, result.State);

            await using var db = fixture.CreateDbContext();
            var missingParentReportId = Guid.NewGuid();
            db.Set<AssessmentReportRevision>().Add(CreateRevision(missingParentReportId, 1));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            var report = new AssessmentReport
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                SubmittedResponseId = responseId,
                QuestionnaireVersion = "3.0",
                ResponseSubmittedUtc = DateTime.UtcNow,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                CreatedByUserId = userId,
                UpdatedByUserId = userId
            };
            db.Set<AssessmentReport>().Add(report);
            await db.SaveChangesAsync();

            db.Set<AssessmentReportRevision>().Add(CreateRevision(report.Id, 1));
            await db.SaveChangesAsync();
            db.Set<AssessmentReportRevision>().Add(CreateRevision(report.Id, 1));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            var badRevision = CreateRevision(report.Id, 0);
            db.Set<AssessmentReportRevision>().Add(badRevision);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            var count = await db.Set<AssessmentReportRevision>().CountAsync(item => item.AssessmentReportId == report.Id);
            Assert.Equal(1, count);
        }
        finally
        {
            await CleanupAsync(fixture, assessmentId, invitationId, responseId);
        }
    }

    private static void AssertSnapshotEquivalent(string expected, string actual)
    {
        _ = ReadinessScoringSnapshotSerializer.Deserialize(expected);
        _ = ReadinessScoringSnapshotSerializer.Deserialize(actual);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)));
    }

    private static AssessmentReportRevision CreateRevision(Guid reportId, int revisionNumber)
    {
        var now = DateTime.UtcNow;
        return new AssessmentReportRevision
        {
            Id = Guid.NewGuid(),
            AssessmentReportId = reportId,
            RevisionNumber = revisionNumber,
            ExecutiveSummary = string.Empty,
            ScopeAndMethodology = string.Empty,
            AssumptionsAndLimitations = string.Empty,
            NextSteps = string.Empty,
            CreatedUtc = now,
            UpdatedUtc = now,
            CreatedByUserId = "integration",
            UpdatedByUserId = "integration"
        };
    }

    private static async Task<AssessmentReportServiceResult> CreateAsync(
        PostgresIntegrationTestFixture fixture,
        Guid assessmentId,
        Guid responseId,
        ClaimsPrincipal caller,
        Barrier? barrier = null)
    {
        await using var db = fixture.CreateDbContext();
        IReadinessScoringService scoringService = barrier is null
            ? new ReadinessScoringService()
            : new BarrierScoringService(barrier);
        var service = new AssessmentReportService(db, scoringService);
        return await service.CreateAsync(assessmentId, responseId, caller);
    }

    private static async Task SeedAsync(
        PostgresIntegrationTestFixture fixture,
        Guid assessmentId,
        Guid invitationId,
        Guid responseId,
        string userId,
        bool draft)
    {
        var now = DateTime.UtcNow;
        await using var db = fixture.CreateDbContext();
        db.Set<Assessment>().Add(new Assessment
        {
            Id = assessmentId,
            Title = "Report service integration",
            SiteName = "Isolated test database",
            ContactEmail = $"{userId}@example.test",
            Status = AssessmentStatus.Submitted,
            OwnerUserId = userId,
            CreatedUtc = now,
            UpdatedUtc = now,
            QuestionnaireVersion = "3.0"
        });
        db.Set<AssessmentInvitation>().Add(new AssessmentInvitation
        {
            Id = invitationId,
            AssessmentId = assessmentId,
            Email = $"{userId}@example.test",
            TokenHash = Guid.NewGuid().ToString("N"),
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(1)
        });
        db.Set<AssessmentResponse>().Add(new AssessmentResponse
        {
            Id = responseId,
            AssessmentId = assessmentId,
            InvitationId = invitationId,
            Data = QuestionnaireResponseV1Serializer.Serialize(ValidSubmission()),
            IsDraft = draft,
            LastSavedUtc = now,
            SubmittedUtc = draft ? null : now
        });
        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(
        PostgresIntegrationTestFixture fixture,
        Guid assessmentId,
        Guid invitationId,
        Guid responseId)
    {
        await using var db = fixture.CreateDbContext();
        var reportIds = await db.Set<AssessmentReport>()
            .Where(report => report.SubmittedResponseId == responseId)
            .Select(report => report.Id)
            .ToListAsync();
        if (reportIds.Count > 0)
        {
            await db.Set<AssessmentReportRevision>().Where(revision => reportIds.Contains(revision.AssessmentReportId)).ExecuteDeleteAsync();
            await db.Set<AssessmentReport>().Where(report => reportIds.Contains(report.Id)).ExecuteDeleteAsync();
        }

        await db.Set<AssessmentResponse>().Where(response => response.Id == responseId).ExecuteDeleteAsync();
        await db.Set<AssessmentInvitation>().Where(invitation => invitation.Id == invitationId).ExecuteDeleteAsync();
        await db.Set<Assessment>().Where(assessment => assessment.Id == assessmentId).ExecuteDeleteAsync();
    }

    private static ClaimsPrincipal Admin(string userId)
        => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Role, "AssessmentAdmin")
        }, "integration"));

    private static QuestionnaireResponseV1 ValidSubmission()
        => new()
        {
            SchemaVersion = "3.0",
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

    private static PostgresIntegrationTestFixture CreateFixtureOrSkip()
    {
        var connection = Environment.GetEnvironmentVariable(PostgresIntegrationTestFixture.ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException($"Set {PostgresIntegrationTestFixture.ConnectionEnvironmentVariable} to run PostgreSQL integration tests.");
        }

        return new PostgresIntegrationTestFixture();
    }

    private sealed class BarrierScoringService(Barrier barrier) : IReadinessScoringService
    {
        private readonly ReadinessScoringService _inner = new();

        public ReadinessScoringResult Score(string? questionnaireVersion, QuestionnaireResponseV1? response)
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(15));
            return _inner.Score(questionnaireVersion, response);
        }
    }
}