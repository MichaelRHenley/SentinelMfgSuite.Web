using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

/// <summary>
/// Direct-SQL tests of TR_AssessmentReportRevisions_IssuedIsImmutable. Issued and Superseded revisions are produced by the
/// real lifecycle services; tampering is then attempted with raw SQL against the real PostgreSQL trigger.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
public sealed class AssessmentReportIssuedImmutabilityPostgresIntegrationTests
{
    private static readonly DateTimeOffset T1 = new(2032, 5, 6, 7, 8, 9, TimeSpan.Zero);
    private static readonly string Sha = new('a', 64);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PassingValidator : IAssessmentReportContentValidator
    {
        public AssessmentReportContentValidationResult Validate(
            ConsultantReportContent content, AssessmentReportContentValidationContext context)
            => new(Array.Empty<ReportContentValidationIssue>());
    }

    private sealed record Chain(Guid ReportId, Guid IssuedId, string UserId, System.Security.Claims.ClaimsPrincipal Caller);

    private static async Task<T> LifecycleAsync<T>(PostgresIntegrationTestFixture fixture, DateTimeOffset at, Func<AssessmentReportLifecycleService, Task<T>> action)
    {
        await using var db = fixture.CreateDbContext();
        return await action(new AssessmentReportLifecycleService(db, new PassingValidator(), new ManualTimeProvider(at)));
    }

    private static async Task<AssessmentReportRevision> LoadAsync(PostgresIntegrationTestFixture fixture, Guid id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private static async Task<Guid> ApproveAsync(PostgresIntegrationTestFixture fixture, Guid revisionId, DateTimeOffset at, System.Security.Claims.ClaimsPrincipal caller)
    {
        var stamp = (await LoadAsync(fixture, revisionId)).ConcurrencyStamp;
        var submit = await LifecycleAsync(fixture, at, s => s.SubmitForReviewAsync(revisionId, stamp, caller));
        Assert.True(submit.Succeeded);
        var approve = await LifecycleAsync(fixture, at, s => s.ApproveAsync(revisionId, submit.ConcurrencyStamp!.Value, caller));
        Assert.True(approve.Succeeded);
        return approve.ConcurrencyStamp!.Value;
    }

    private static async Task<Chain> IssuedAsync(PostgresIntegrationTestFixture fixture)
    {
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        await using (var db = fixture.CreateDbContext())
        {
            // Give the revision frozen scoring/content/template values so tampering is a real change.
            await db.Database.ExecuteSqlRawAsync(
                Raw("UPDATE \"AssessmentReportRevisions\" SET \"ReportTemplateVersion\" = 't1' " +
                $"WHERE \"Id\" = '{chain.RevisionId}'"));
        }

        var stamp = (await LoadAsync(fixture, chain.RevisionId)).ConcurrencyStamp;
        await using (var db = fixture.CreateDbContext())
        {
            var saved = await new AssessmentReportRevisionContentService(db, new PassingValidator())
                .SaveDraftAsync(chain.RevisionId, stamp, ConsultantReportContent.CreateEmpty() with { ExecutiveSummary = new ExecutiveSummarySection(Narrative: "frozen") }, caller);
            Assert.Equal(AssessmentReportRevisionContentState.Saved, saved.State);
        }

        var approved = await ApproveAsync(fixture, chain.RevisionId, T1, caller);
        Assert.True((await LifecycleAsync(fixture, T1.AddHours(1), s => s.IssueAsync(chain.RevisionId, approved, caller))).Succeeded);
        return new Chain(chain.ReportId, chain.RevisionId, chain.UserId, caller);
    }

    /// <summary>Issues a correction through the services so the original becomes Superseded. Returns the replacement id.</summary>
    private static async Task<Guid> SupersedeAsync(PostgresIntegrationTestFixture fixture, Chain chain)
    {
        var source = await LoadAsync(fixture, chain.IssuedId);
        var created = await LifecycleAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, source.ConcurrencyStamp, "fix", chain.Caller));
        Assert.True(created.Succeeded);
        var replacementId = created.RevisionId!.Value;
        var approved = await ApproveAsync(fixture, replacementId, T1.AddHours(3), chain.Caller);
        Assert.True((await LifecycleAsync(fixture, T1.AddHours(4), s => s.IssueAsync(replacementId, approved, chain.Caller))).Succeeded);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, (await LoadAsync(fixture, chain.IssuedId)).Status);
        return replacementId;
    }

    private static async Task<string?> TryExecuteAsync(PostgresIntegrationTestFixture fixture, string sql)
    {
        await using var db = fixture.CreateDbContext();
        try
        {
            await db.Database.ExecuteSqlRawAsync(Raw(sql));
            return null;
        }
        catch (Exception ex)
        {
            var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
            Assert.NotNull(pg);
            return pg!.SqlState;
        }
    }

    private static string Raw(string sql) => sql.Replace("{", "{{").Replace("}", "}}");

    private static string Update(Guid id, string set) => $"UPDATE \"AssessmentReportRevisions\" SET {set} WHERE \"Id\" = '{id}'";

    private static async Task AssertRejectedAndUnchangedAsync(PostgresIntegrationTestFixture fixture, Guid id, string set)
    {
        var before = await LoadAsync(fixture, id);
        Assert.Equal("23514", await TryExecuteAsync(fixture, Update(id, set)));
        var after = await LoadAsync(fixture, id);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.ExecutiveSummary, after.ExecutiveSummary);
        Assert.Equal(before.ConsultantContentJson, after.ConsultantContentJson);
        Assert.Equal(before.ConsultantContentSchemaVersion, after.ConsultantContentSchemaVersion);
        Assert.Equal(before.ScoringSnapshot, after.ScoringSnapshot);
        Assert.Equal(before.ReportTemplateVersion, after.ReportTemplateVersion);
        Assert.Equal(before.IssuedUtc, after.IssuedUtc);
        Assert.Equal(before.IssuedByUserId, after.IssuedByUserId);
        Assert.Equal(before.SupersededByRevisionId, after.SupersededByRevisionId);
        Assert.Equal(before.PdfStorageKey, after.PdfStorageKey);
        Assert.Equal(before.PdfSha256, after.PdfSha256);
        Assert.Equal(before.PdfLengthBytes, after.PdfLengthBytes);
        Assert.Equal(before.ConcurrencyStamp, after.ConcurrencyStamp);
    }

    public static IEnumerable<object[]> FrozenChanges() =>
    [
        ["\"ConsultantContentJson\" = '{\"v\":2}'::jsonb"],
        ["\"ScoringSnapshot\" = '{\"score\":99}'::jsonb"],
        ["\"ScoringSnapshot\" = NULL"],
        ["\"ReportTemplateVersion\" = 'tampered'"],
        ["\"ConsultantContentSchemaVersion\" = '9.9'"],
        ["\"ScoringContractVersion\" = 'tampered'"],
        ["\"ExecutiveSummary\" = 'tampered'"],
        ["\"IssuedByUserId\" = 'attacker'"],
        ["\"IssuedUtc\" = \"IssuedUtc\" + interval '1 day'"],
        ["\"ApprovedByUserId\" = 'attacker'"],
        ["\"ApprovedUtc\" = \"ApprovedUtc\" + interval '1 day'"],
        ["\"SubmittedForReviewByUserId\" = 'attacker'"],
        ["\"RevisionNumber\" = 99"],
        ["\"BasedOnRevisionId\" = NULL"],
        ["\"CorrectionReason\" = 'tampered'"],
        ["\"CreatedByUserId\" = 'attacker'"]
    ];

    [Theory]
    [MemberData(nameof(FrozenChanges))]
    [Trait("Category", "PostgresIntegration")]
    public async Task Issued_Frozen_Column_Changes_Are_Rejected_And_Leave_The_Row_Unchanged(string set)
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await IssuedAsync(fixture);
        if (set.StartsWith("\"BasedOnRevisionId\"") || set.StartsWith("\"CorrectionReason\""))
        {
            // Initial revisions have no lineage; make the case meaningful by testing against a correction instead.
            var replacementId = await SupersedeAsync(fixture, chain);
            await AssertRejectedAndUnchangedAsync(fixture, replacementId, set);
            return;
        }

        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, set);
    }

    [Theory]
    [MemberData(nameof(FrozenChanges))]
    [Trait("Category", "PostgresIntegration")]
    public async Task Superseded_Frozen_Column_Changes_Are_Rejected_And_Leave_The_Row_Unchanged(string set)
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await IssuedAsync(fixture);
        await SupersedeAsync(fixture, chain);
        if (set.StartsWith("\"BasedOnRevisionId\"") || set.StartsWith("\"CorrectionReason\""))
        {
            // The superseded original has no lineage, so setting these on it is a prohibited change too.
            set = set.StartsWith("\"BasedOnRevisionId\"") ? $"\"BasedOnRevisionId\" = '{Guid.NewGuid()}'" : "\"CorrectionReason\" = 'x', \"BasedOnRevisionId\" = \"BasedOnRevisionId\"";
        }

        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, set);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Combined_Status_And_Content_Tampering_Is_Rejected()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await IssuedAsync(fixture);
        var other = Guid.NewGuid();

        // Evaded-status attempts: the guard keys off the OLD row status.
        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, "\"Status\" = 4, \"SupersededByRevisionId\" = '" + other + "', \"ConsultantContentJson\" = '{\"v\":2}'::jsonb");
        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, "\"Status\" = 2, \"IssuedUtc\" = NULL, \"IssuedByUserId\" = NULL, \"ConsultantContentJson\" = '{\"v\":2}'::jsonb");
        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, "\"Status\" = 0, \"ApprovedUtc\" = NULL, \"ApprovedByUserId\" = NULL, \"IssuedUtc\" = NULL, \"IssuedByUserId\" = NULL, \"SubmittedForReviewUtc\" = NULL, \"SubmittedForReviewByUserId\" = NULL");
        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, "\"Status\" = 5, \"IssuedUtc\" = NULL, \"IssuedByUserId\" = NULL");
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Valid_Issued_To_Superseded_Through_Services_Is_Accepted_And_Only_Allowed_Columns_Change()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await IssuedAsync(fixture);
        var before = await LoadAsync(fixture, chain.IssuedId);

        var replacementId = await SupersedeAsync(fixture, chain);

        var after = await LoadAsync(fixture, chain.IssuedId);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, after.Status);
        Assert.Equal(replacementId, after.SupersededByRevisionId);
        Assert.Equal(before.ConsultantContentJson, after.ConsultantContentJson);
        Assert.Equal(before.ScoringSnapshot, after.ScoringSnapshot);
        Assert.Equal(before.ReportTemplateVersion, after.ReportTemplateVersion);
        Assert.Equal(before.IssuedUtc, after.IssuedUtc);
        Assert.Equal(before.IssuedByUserId, after.IssuedByUserId);
        Assert.Equal(before.ApprovedUtc, after.ApprovedUtc);
        Assert.NotEqual(before.ConcurrencyStamp, after.ConcurrencyStamp);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Superseded_Revision_Cannot_Leave_Superseded_Or_Change_Its_Replacement()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await IssuedAsync(fixture);
        await SupersedeAsync(fixture, chain);

        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, "\"Status\" = 3, \"SupersededByRevisionId\" = NULL");
        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, "\"Status\" = 2, \"SupersededByRevisionId\" = NULL");
        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, "\"SupersededByRevisionId\" = '" + Guid.NewGuid() + "'");
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Complete_First_Pdf_Finalization_Is_Accepted_And_Changes_Nothing_Else()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await IssuedAsync(fixture);
        var before = await LoadAsync(fixture, chain.IssuedId);

        Assert.Null(await TryExecuteAsync(fixture, Update(chain.IssuedId, $"\"PdfStorageKey\" = 'reports/a.pdf', \"PdfSha256\" = '{Sha}', \"PdfLengthBytes\" = 1234")));

        var after = await LoadAsync(fixture, chain.IssuedId);
        Assert.Equal("reports/a.pdf", after.PdfStorageKey);
        Assert.Equal(Sha, after.PdfSha256);
        Assert.Equal(1234, after.PdfLengthBytes);
        Assert.Equal(AssessmentReportRevisionStatus.Issued, after.Status);
        Assert.Equal(before.ConsultantContentJson, after.ConsultantContentJson);
        Assert.Equal(before.ScoringSnapshot, after.ScoringSnapshot);
        Assert.Equal(before.IssuedUtc, after.IssuedUtc);
    }

    [Theory]
    [InlineData("\"PdfStorageKey\" = 'reports/a.pdf'")]
    [InlineData("\"PdfSha256\" = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    [InlineData("\"PdfLengthBytes\" = 10")]
    [InlineData("\"PdfStorageKey\" = 'k', \"PdfSha256\" = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    [InlineData("\"PdfStorageKey\" = 'k', \"PdfSha256\" = 'short', \"PdfLengthBytes\" = 10")]
    [InlineData("\"PdfStorageKey\" = '   ', \"PdfSha256\" = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', \"PdfLengthBytes\" = 10")]
    [InlineData("\"PdfStorageKey\" = 'k', \"PdfSha256\" = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', \"PdfLengthBytes\" = 0")]
    [Trait("Category", "PostgresIntegration")]
    public async Task Partial_Or_Malformed_Pdf_Metadata_Is_Rejected(string set)
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await IssuedAsync(fixture);
        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, set);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Second_Pdf_Write_And_Pdf_Clearing_Are_Rejected()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await IssuedAsync(fixture);
        Assert.Null(await TryExecuteAsync(fixture, Update(chain.IssuedId, $"\"PdfStorageKey\" = 'reports/a.pdf', \"PdfSha256\" = '{Sha}', \"PdfLengthBytes\" = 1234")));

        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, $"\"PdfStorageKey\" = 'reports/b.pdf', \"PdfSha256\" = '{new string('b', 64)}', \"PdfLengthBytes\" = 99");
        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, "\"PdfLengthBytes\" = 1235");
        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, "\"PdfStorageKey\" = NULL, \"PdfSha256\" = NULL, \"PdfLengthBytes\" = NULL");
        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, "\"PdfSha256\" = NULL");
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Pdf_Metadata_Cannot_Be_Added_To_A_Superseded_Revision_Or_Kept_Through_Supersession_Tampering()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await IssuedAsync(fixture);
        await SupersedeAsync(fixture, chain);

        await AssertRejectedAndUnchangedAsync(fixture, chain.IssuedId, $"\"PdfStorageKey\" = 'reports/a.pdf', \"PdfSha256\" = '{Sha}', \"PdfLengthBytes\" = 1234");
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Pdf_Metadata_Cannot_Be_Set_While_Approving_Into_Issued_Or_On_Unissued_Revisions()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(fixture);

        // Draft revision: not covered by the guard's trigger WHEN clause, so only a transition into Issued is checked.
        var pdf = $"\"PdfStorageKey\" = 'reports/a.pdf', \"PdfSha256\" = '{Sha}', \"PdfLengthBytes\" = 1234";
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var approved = await ApproveAsync(fixture, chain.RevisionId, T1, caller);
        Assert.Equal("23514", await TryExecuteAsync(fixture, Update(chain.RevisionId, $"\"Status\" = 3, \"IssuedUtc\" = now(), \"IssuedByUserId\" = 'x', {pdf}")));
        var stored = await LoadAsync(fixture, chain.RevisionId);
        Assert.Equal(AssessmentReportRevisionStatus.Approved, stored.Status);
        Assert.Null(stored.PdfStorageKey);
        Assert.Equal(approved, stored.ConcurrencyStamp);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Unchanged_Value_Updates_And_Mutable_Columns_Are_Accepted_On_Issued_And_Superseded_Revisions()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await IssuedAsync(fixture);

        const string noOp = "\"ExecutiveSummary\" = \"ExecutiveSummary\", \"ConsultantContentJson\" = \"ConsultantContentJson\", \"ScoringSnapshot\" = \"ScoringSnapshot\", " +
                            "\"IssuedUtc\" = \"IssuedUtc\", \"BasedOnRevisionId\" = \"BasedOnRevisionId\", \"PdfStorageKey\" = \"PdfStorageKey\", \"PdfSha256\" = \"PdfSha256\", \"PdfLengthBytes\" = \"PdfLengthBytes\"";
        Assert.Null(await TryExecuteAsync(fixture, Update(chain.IssuedId, noOp)));
        Assert.Null(await TryExecuteAsync(fixture, Update(chain.IssuedId, "\"UpdatedUtc\" = now(), \"UpdatedByUserId\" = 'svc', \"ConcurrencyStamp\" = gen_random_uuid()")));

        await SupersedeAsync(fixture, chain);
        Assert.Null(await TryExecuteAsync(fixture, Update(chain.IssuedId, noOp)));
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, (await LoadAsync(fixture, chain.IssuedId)).Status);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Correction_Draft_Does_Not_Inherit_Pdf_Metadata_And_Remains_Editable()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var chain = await IssuedAsync(fixture);
        Assert.Null(await TryExecuteAsync(fixture, Update(chain.IssuedId, $"\"PdfStorageKey\" = 'reports/a.pdf', \"PdfSha256\" = '{Sha}', \"PdfLengthBytes\" = 1234")));
        var source = await LoadAsync(fixture, chain.IssuedId);

        var created = await LifecycleAsync(fixture, T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, source.ConcurrencyStamp, "fix", chain.Caller));
        Assert.True(created.Succeeded);
        var draft = await LoadAsync(fixture, created.RevisionId!.Value);
        Assert.Null(draft.PdfStorageKey);
        Assert.Null(draft.PdfSha256);
        Assert.Null(draft.PdfLengthBytes);
        Assert.Null(await TryExecuteAsync(fixture, Update(draft.Id, "\"ExecutiveSummary\" = 'draft edit'")));
    }
}
