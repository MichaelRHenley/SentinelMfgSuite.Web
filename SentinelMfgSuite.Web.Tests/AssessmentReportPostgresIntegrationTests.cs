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

[Collection(PostgresIntegrationCollection.Name)]
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
            Assert.Equal(1, await verifyDb.Set<AssessmentReportTransitionHistory>().CountAsync(item => item.AssessmentReportId == first.Report.Id));
            Assert.Equal(1, await verifyDb.Set<AssessmentReportTransitionHistory>().CountAsync(item => item.AssessmentReportId == concurrentResults[0].Report!.Id
                && item.EventType == AssessmentReportTransitionEventType.Created));

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

    private const short Draft = 0, InReview = 1, Approved = 2, Issued = 3, Superseded = 4, Rejected = 5;
    private const short Created = 1, Transition = 2;

    internal sealed record ReportChain(Guid AssessmentId, Guid InvitationId, Guid ResponseId, Guid ReportId, Guid RevisionId, string UserId);

    internal static async Task<ReportChain> CreateChainAsync(PostgresIntegrationTestFixture fixture)
    {
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var userId = $"report-history-{Guid.NewGuid():N}";
        await SeedAsync(fixture, assessmentId, invitationId, responseId, userId, false);
        var result = await CreateAsync(fixture, assessmentId, responseId, Admin(userId));
        Assert.Equal(AssessmentReportServiceState.Created, result.State);
        return new ReportChain(assessmentId, invitationId, responseId, result.Report!.Id, result.Report.Revisions.Single().Id, userId);
    }

    private static async Task<Guid> AddRevisionAsync(PostgresIntegrationTestFixture fixture, Guid reportId, int number)
    {
        await using var db = fixture.CreateDbContext();
        var revision = CreateRevision(reportId, number);
        db.Set<AssessmentReportRevision>().Add(revision);
        await db.SaveChangesAsync();
        return revision.Id;
    }

    private static async Task InsertHistoryAsync(
        PostgresIntegrationTestFixture fixture,
        Guid reportId,
        Guid revisionId,
        short eventType,
        short? previous,
        short next,
        string? reason = null,
        Guid? replaced = null,
        Guid? replacement = null,
        string actor = "actor-1",
        string correlation = "corr-1")
    {
        await using var db = fixture.CreateDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ""AssessmentReportTransitionHistory""
(""Id"", ""AssessmentReportId"", ""AssessmentReportRevisionId"", ""EventType"", ""PreviousStatus"", ""NewStatus"", ""ActorUserId"", ""OccurredUtc"", ""Reason"", ""ReplacedRevisionId"", ""ReplacementRevisionId"", ""CorrelationId"")
VALUES ({Guid.NewGuid()}, {reportId}, {revisionId}, {eventType}, {previous}, {next}, {actor}, {DateTime.UtcNow}, {reason}, {replaced}, {replacement}, {correlation})");
    }

    private static async Task AssertViolationAsync(Func<Task> action, string sqlState)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(action);
        var pg = ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException;
        Assert.NotNull(pg);
        Assert.Equal(sqlState, pg!.SqlState);
    }

    private const string CheckViolation = "23514";
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";
    private const string RestrictViolation = "23001";

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CreateAsync_Writes_Single_Created_History_Event_With_Authenticated_Actor()
    {
        var fixture = CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var before = DateTime.UtcNow.AddSeconds(-5);
        var chain = await CreateChainAsync(fixture);
        try
        {
            await using var db = fixture.CreateDbContext();
            var history = await db.Set<AssessmentReportTransitionHistory>().SingleAsync(item => item.AssessmentReportId == chain.ReportId);
            var revision = await db.Set<AssessmentReportRevision>().SingleAsync(item => item.Id == chain.RevisionId);
            Assert.Equal(AssessmentReportTransitionEventType.Created, history.EventType);
            Assert.Null(history.PreviousStatus);
            Assert.Equal(AssessmentReportRevisionStatus.Draft, history.NewStatus);
            Assert.Equal(chain.UserId, history.ActorUserId);
            Assert.Equal(chain.RevisionId, history.AssessmentReportRevisionId);
            Assert.Equal(revision.CreatedUtc, history.OccurredUtc);
            Assert.InRange(history.OccurredUtc, before, DateTime.UtcNow.AddSeconds(5));
            Assert.False(string.IsNullOrWhiteSpace(history.CorrelationId));
            Assert.Null(history.Reason);
            Assert.Null(history.ReplacedRevisionId);
            Assert.Null(history.ReplacementRevisionId);

            var retry = await CreateAsync(fixture, chain.AssessmentId, chain.ResponseId, Admin(chain.UserId));
            Assert.Equal(AssessmentReportServiceState.AlreadyExists, retry.State);
            Assert.Equal(1, await db.Set<AssessmentReportTransitionHistory>().CountAsync(item => item.AssessmentReportId == chain.ReportId));
        }
        finally
        {
            await CleanupAsync(fixture, chain.AssessmentId, chain.InvitationId, chain.ResponseId);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task History_Constraints_Accept_All_Six_Committed_Transitions_And_Valid_Issuance_Shapes()
    {
        var fixture = CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await CreateChainAsync(fixture);
        try
        {
            var r = chain.RevisionId;
            var other = await AddRevisionAsync(fixture, chain.ReportId, 2);
            var third = await AddRevisionAsync(fixture, chain.ReportId, 3);

            await InsertHistoryAsync(fixture, chain.ReportId, r, Transition, Draft, InReview);
            await InsertHistoryAsync(fixture, chain.ReportId, r, Transition, InReview, Approved);
            await InsertHistoryAsync(fixture, chain.ReportId, r, Transition, InReview, Rejected, reason: "Needs rework");
            await InsertHistoryAsync(fixture, chain.ReportId, other, Transition, Approved, Rejected, reason: new string('x', 2000));
            // initial issuance (no predecessor)
            await InsertHistoryAsync(fixture, chain.ReportId, r, Transition, Approved, Issued);
            // replacement issuance (predecessor) and the paired supersession
            await InsertHistoryAsync(fixture, chain.ReportId, third, Transition, Approved, Issued, replaced: r);
            await InsertHistoryAsync(fixture, chain.ReportId, r, Transition, Issued, Superseded, replacement: third);

            await using var db = fixture.CreateDbContext();
            Assert.Equal(7, await db.Set<AssessmentReportTransitionHistory>()
                .CountAsync(item => item.AssessmentReportId == chain.ReportId && item.EventType == AssessmentReportTransitionEventType.Transition));
        }
        finally
        {
            await CleanupAsync(fixture, chain.AssessmentId, chain.InvitationId, chain.ResponseId);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task History_Constraints_Reject_Invalid_Shapes_Pairs_Reasons_And_Links()
    {
        var fixture = CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await CreateChainAsync(fixture);
        var otherChain = await CreateChainAsync(fixture);
        try
        {
            var r = chain.RevisionId;
            var rep = chain.ReportId;
            var second = await AddRevisionAsync(fixture, rep, 2);

            // Event type / status values
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, 9, null, Draft), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Draft, 6), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, 7, InReview), CheckViolation);

            // Created shape
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Created, Draft, Draft), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Created, null, InReview), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Created, null, Draft, reason: "no"), CheckViolation);

            // Transition requires previous status and an allowed pair
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, null, InReview), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Draft, Rejected, reason: "x"), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Approved, Superseded, replacement: r), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Draft, Approved), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Rejected, Draft), CheckViolation);

            // Reasons
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, InReview, Rejected), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, InReview, Rejected, reason: ""), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, InReview, Rejected, reason: "   "), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, InReview, Rejected, reason: " padded"), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, InReview, Rejected, reason: "padded "), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, InReview, Rejected, reason: new string('x', 2001)), "22001");
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Draft, InReview, reason: "not allowed"), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Approved, Issued, reason: "not allowed"), CheckViolation);

            // Identifiers
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Draft, InReview, actor: "  "), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Draft, InReview, correlation: ""), CheckViolation);

            // Replacement links
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Issued, Superseded), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Issued, Superseded, replacement: second), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Approved, Issued, replaced: second), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Draft, InReview, replaced: r), CheckViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Draft, InReview, replacement: r), CheckViolation);

            // Cross-report links rejected by composite foreign keys
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, otherChain.RevisionId, Transition, Draft, InReview), ForeignKeyViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Approved, Issued, replaced: otherChain.RevisionId), ForeignKeyViolation);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Issued, Superseded, replacement: otherChain.RevisionId), ForeignKeyViolation);

            // Uniqueness
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, r, Created, null, Draft), UniqueViolation);
            await InsertHistoryAsync(fixture, rep, second, Transition, Draft, InReview);
            await AssertViolationAsync(() => InsertHistoryAsync(fixture, rep, second, Transition, Draft, InReview), UniqueViolation);

            await using var db = fixture.CreateDbContext();
            Assert.Equal(1, await db.Set<AssessmentReportTransitionHistory>().CountAsync(item => item.AssessmentReportRevisionId == second));
        }
        finally
        {
            await CleanupAsync(fixture, chain.AssessmentId, chain.InvitationId, chain.ResponseId);
            await CleanupAsync(fixture, otherChain.AssessmentId, otherChain.InvitationId, otherChain.ResponseId);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task History_Is_Append_Only_And_Failed_Operations_Leave_No_Partial_Rows()
    {
        var fixture = CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await CreateChainAsync(fixture);
        try
        {
            await using var db = fixture.CreateDbContext();
            var rep = chain.ReportId;
            var before = await db.Set<AssessmentReportTransitionHistory>().CountAsync(item => item.AssessmentReportId == rep);
            Assert.Equal(1, before);

            await AssertViolationAsync(
                () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AssessmentReportTransitionHistory\" SET \"CorrelationId\" = 'tampered' WHERE \"AssessmentReportId\" = {rep}"),
                RestrictViolation);
            await AssertViolationAsync(
                () => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AssessmentReportTransitionHistory\" WHERE \"AssessmentReportId\" = {rep}"),
                RestrictViolation);
            await AssertViolationAsync(
                () => db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"AssessmentReportTransitionHistory\""),
                RestrictViolation);

            // EF-based mutation is also rejected by the database
            await using (var efDb = fixture.CreateDbContext())
            {
                var row = await efDb.Set<AssessmentReportTransitionHistory>().SingleAsync(item => item.AssessmentReportId == rep);
                row.CorrelationId = "tampered";
                var efException = await Assert.ThrowsAsync<DbUpdateException>(() => efDb.SaveChangesAsync());
                var efPg = Assert.IsType<Npgsql.PostgresException>(efException.InnerException);
                Assert.Equal(RestrictViolation, efPg.SqlState);
            }

            // Revisions that have history
            await AssertViolationAsync(
                () => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AssessmentReportRevisions\" WHERE \"Id\" = {chain.RevisionId}"),
                ForeignKeyViolation);

            // A failed statement inside a transaction rolls back earlier valid inserts
            var rolledBackRevision = await AddRevisionAsync(fixture, rep, 2);
            await using (var txDb = fixture.CreateDbContext())
            {
                await using var tx = await txDb.Database.BeginTransactionAsync();
                await txDb.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ""AssessmentReportTransitionHistory""
(""Id"", ""AssessmentReportId"", ""AssessmentReportRevisionId"", ""EventType"", ""PreviousStatus"", ""NewStatus"", ""ActorUserId"", ""OccurredUtc"", ""CorrelationId"")
VALUES ({Guid.NewGuid()}, {rep}, {rolledBackRevision}, {Transition}, {Draft}, {InReview}, 'actor', {DateTime.UtcNow}, 'corr')");
                await AssertViolationAsync(
                    () => txDb.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ""AssessmentReportTransitionHistory""
(""Id"", ""AssessmentReportId"", ""AssessmentReportRevisionId"", ""EventType"", ""PreviousStatus"", ""NewStatus"", ""ActorUserId"", ""OccurredUtc"", ""CorrelationId"")
VALUES ({Guid.NewGuid()}, {rep}, {rolledBackRevision}, {Transition}, {Draft}, {Rejected}, 'actor', {DateTime.UtcNow}, 'corr')"),
                    CheckViolation);
                await tx.RollbackAsync();
            }

            await using var verifyDb = fixture.CreateDbContext();
            Assert.Equal(0, await verifyDb.Set<AssessmentReportTransitionHistory>().CountAsync(item => item.AssessmentReportRevisionId == rolledBackRevision));
            Assert.Equal(1, await verifyDb.Set<AssessmentReportTransitionHistory>().CountAsync(item => item.AssessmentReportId == rep));
        }
        finally
        {
            await CleanupAsync(fixture, chain.AssessmentId, chain.InvitationId, chain.ResponseId);
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
            // Transition history is append-only and protected by triggers, so any chain that has
            // history is intentionally left in place until an authorized maintenance reset.
            var protectedReportIds = await db.Set<AssessmentReportTransitionHistory>()
                .Where(history => reportIds.Contains(history.AssessmentReportId))
                .Select(history => history.AssessmentReportId)
                .Distinct()
                .ToListAsync();
            if (protectedReportIds.Count > 0)
            {
                return;
            }

            await db.Set<AssessmentReportRevision>().Where(revision => reportIds.Contains(revision.AssessmentReportId)).ExecuteDeleteAsync();
            await db.Set<AssessmentReport>().Where(report => reportIds.Contains(report.Id)).ExecuteDeleteAsync();
        }

        await db.Set<AssessmentResponse>().Where(response => response.Id == responseId).ExecuteDeleteAsync();
        await db.Set<AssessmentInvitation>().Where(invitation => invitation.Id == invitationId).ExecuteDeleteAsync();
        await db.Set<Assessment>().Where(assessment => assessment.Id == assessmentId).ExecuteDeleteAsync();
    }

    internal static ClaimsPrincipal Admin(string userId)
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

    internal static PostgresIntegrationTestFixture CreateFixtureOrSkip()
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