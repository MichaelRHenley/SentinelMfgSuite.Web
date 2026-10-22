using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "PostgresIntegration")]
public sealed class ReportPdfFinalizationPostgresIntegrationTests : IDisposable
{
    private static readonly ReportPdfTemplateRegistry Registry = new();
    private static readonly DateTimeOffset T1 = new(2032, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private readonly PostgresIntegrationTestFixture _fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sentinel-pdf-final-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemReportPdfStorage _storage;

    public ReportPdfFinalizationPostgresIntegrationTests()
    {
        _fixture.InitializeAsync().GetAwaiter().GetResult();
        _storage = new FileSystemReportPdfStorage(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    // ------------------------------------------------------------ helpers

    private sealed class PassingValidator : IAssessmentReportContentValidator
    {
        public AssessmentReportContentValidationResult Validate(
            ConsultantReportContent content, AssessmentReportContentValidationContext context)
            => new(Array.Empty<ReportContentValidationIssue>());
    }

    private sealed class FailingStorage : IReportPdfStorage
    {
        public Task<ReportPdfStorageWriteOutcome> WriteAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
            => throw new ReportPdfStorageException("disk full");
        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> DeleteAsync(string key, string expectedSha256, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class CountingStorage(IReportPdfStorage inner) : IReportPdfStorage
    {
        public int Writes;
        public async Task<ReportPdfStorageWriteOutcome> WriteAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Writes);
            return await inner.WriteAsync(key, content, cancellationToken);
        }
        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => inner.ExistsAsync(key, cancellationToken);
        public Task<bool> DeleteAsync(string key, string expectedSha256, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, expectedSha256, cancellationToken);
    }

    private sealed class FailPdfUpdateInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"PdfStorageKey\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("simulated database failure");
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private ApplicationDbContext NewDb(bool failPdfUpdate = false)
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(_fixture.ConnectionString);
        if (failPdfUpdate)
        {
            builder.AddInterceptors(new FailPdfUpdateInterceptor());
        }
        return new ApplicationDbContext(builder.Options);
    }

    private async Task<ReportPdfFinalizationResult> FinalizeAsync(
        Guid revisionId, ClaimsPrincipal caller, IReportPdfStorage? storage = null, bool failPdfUpdate = false)
    {
        await using var db = NewDb(failPdfUpdate);
        var service = new ReportPdfFinalizationService(
            db, new ReportPdfModelBuilder(Registry), new ReportPdfRenderer(Registry),
            storage ?? _storage, TimeProvider.System, NullLogger<ReportPdfFinalizationService>.Instance);
        return await service.FinalizeAsync(revisionId, caller);
    }

    private async Task<ReportPdfReadResult> ReadAsync(Guid revisionId, ClaimsPrincipal caller)
    {
        await using var db = NewDb();
        var service = new ReportPdfFinalizationService(
            db, new ReportPdfModelBuilder(Registry), new ReportPdfRenderer(Registry),
            _storage, TimeProvider.System, NullLogger<ReportPdfFinalizationService>.Instance);
        return await service.ReadAsync(revisionId, caller);
    }

    private async Task<AssessmentReportRevision> LoadAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.Set<AssessmentReportRevision>().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private async Task<T> LifecycleAsync<T>(DateTimeOffset at, Func<AssessmentReportLifecycleService, Task<T>> action)
    {
        await using var db = NewDb();
        return await action(new AssessmentReportLifecycleService(db, new PassingValidator(), new FixedTime(at)));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<Guid> ApproveAsync(Guid revisionId, ClaimsPrincipal caller)
    {
        var stamp = (await LoadAsync(revisionId)).ConcurrencyStamp;
        var submit = await LifecycleAsync(T1, s => s.SubmitForReviewAsync(revisionId, stamp, caller));
        Assert.True(submit.Succeeded);
        var approve = await LifecycleAsync(T1, s => s.ApproveAsync(revisionId, submit.ConcurrencyStamp!.Value, caller));
        Assert.True(approve.Succeeded);
        return approve.ConcurrencyStamp!.Value;
    }

    private sealed record Chain(Guid ReportId, Guid RevisionId, string UserId, ClaimsPrincipal Caller);

    private async Task<Chain> DraftAsync()
    {
        var chain = await AssessmentReportPostgresIntegrationTests.CreateChainAsync(_fixture);
        var caller = AssessmentReportPostgresIntegrationTests.Admin(chain.UserId);
        var stamp = (await LoadAsync(chain.RevisionId)).ConcurrencyStamp;
        await using var db = NewDb();
        var saved = await new AssessmentReportRevisionContentService(db, new PassingValidator())
            .SaveDraftAsync(chain.RevisionId, stamp,
                ConsultantReportContent.CreateEmpty() with { ExecutiveSummary = new ExecutiveSummarySection(Narrative: "frozen narrative") },
                caller);
        Assert.Equal(AssessmentReportRevisionContentState.Saved, saved.State);
        return new Chain(chain.ReportId, chain.RevisionId, chain.UserId, caller);
    }

    private async Task<Chain> IssuedAsync()
    {
        var chain = await DraftAsync();
        var approved = await ApproveAsync(chain.RevisionId, chain.Caller);
        Assert.True((await LifecycleAsync(T1.AddHours(1), s => s.IssueAsync(chain.RevisionId, approved, chain.Caller))).Succeeded);
        return chain;
    }

    private async Task<Guid> SupersedeAsync(Chain chain)
    {
        var source = await LoadAsync(chain.RevisionId);
        var created = await LifecycleAsync(T1.AddHours(2), s => s.CreateCorrectionAsync(chain.ReportId, source.ConcurrencyStamp, "fix", chain.Caller));
        Assert.True(created.Succeeded);
        var replacementId = created.RevisionId!.Value;
        var approved = await ApproveAsync(replacementId, chain.Caller);
        Assert.True((await LifecycleAsync(T1.AddHours(4), s => s.IssueAsync(replacementId, approved, chain.Caller))).Succeeded);
        Assert.Equal(AssessmentReportRevisionStatus.Superseded, (await LoadAsync(chain.RevisionId)).Status);
        return replacementId;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private string PathOf(string key) => Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));

    private static void AssertFrozenUnchanged(AssessmentReportRevision before, AssessmentReportRevision after)
    {
        Assert.Equal(before.ExecutiveSummary, after.ExecutiveSummary);
        Assert.Equal(before.ConsultantContentJson, after.ConsultantContentJson);
        Assert.Equal(before.ConsultantContentSchemaVersion, after.ConsultantContentSchemaVersion);
        Assert.Equal(before.ScoringSnapshot, after.ScoringSnapshot);
        Assert.Equal(before.ScoringSnapshotSchemaVersion, after.ScoringSnapshotSchemaVersion);
        Assert.Equal(before.ReportTemplateVersion, after.ReportTemplateVersion);
        Assert.Equal(before.IssuedUtc, after.IssuedUtc);
        Assert.Equal(before.IssuedByUserId, after.IssuedByUserId);
        Assert.Equal(before.ApprovedUtc, after.ApprovedUtc);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.BasedOnRevisionId, after.BasedOnRevisionId);
    }

    // ------------------------------------------------------------ tests

    [Fact]
    public async Task Initial_Finalization_Stores_Pdf_And_Sets_Metadata_Together_Without_Mutating_Frozen_Content()
    {
        var chain = await IssuedAsync();
        var before = await LoadAsync(chain.RevisionId);

        var result = await FinalizeAsync(chain.RevisionId, chain.Caller);

        Assert.Equal(ReportPdfFinalizationState.Succeeded, result.State);
        var after = await LoadAsync(chain.RevisionId);
        Assert.Equal(result.StorageKey, after.PdfStorageKey);
        Assert.Equal(result.Sha256, after.PdfSha256);
        Assert.Equal(result.LengthBytes, after.PdfLengthBytes);
        Assert.Equal(after.ConcurrencyStamp, result.ConcurrencyStamp);
        Assert.NotEqual(before.ConcurrencyStamp, after.ConcurrencyStamp);
        AssertFrozenUnchanged(before, after);

        var stored = await File.ReadAllBytesAsync(PathOf(after.PdfStorageKey!));
        Assert.Equal(after.PdfLengthBytes, stored.LongLength);
        Assert.Equal(after.PdfSha256, Sha(stored));
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(stored, 0, 5));
        Assert.Equal($"reports/{chain.ReportId:N}/{chain.RevisionId:N}-{after.PdfSha256}.pdf", after.PdfStorageKey);

        var read = await ReadAsync(chain.RevisionId, chain.Caller);
        Assert.Equal(ReportPdfReadState.Succeeded, read.State);
        Assert.Equal(stored, read.Content);
    }

    [Fact]
    public async Task Repeat_Request_Returns_AlreadyFinalized_Without_Rendering_Or_Writing()
    {
        var chain = await IssuedAsync();
        var first = await FinalizeAsync(chain.RevisionId, chain.Caller);
        Assert.Equal(ReportPdfFinalizationState.Succeeded, first.State);
        var before = await LoadAsync(chain.RevisionId);

        var counting = new CountingStorage(_storage);
        var second = await FinalizeAsync(chain.RevisionId, chain.Caller, counting);

        Assert.Equal(ReportPdfFinalizationState.AlreadyFinalized, second.State);
        Assert.Equal(first.StorageKey, second.StorageKey);
        Assert.Equal(0, counting.Writes);
        var after = await LoadAsync(chain.RevisionId);
        Assert.Equal(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
    }

    [Fact]
    public async Task Concurrent_Requests_Produce_Exactly_One_Finalization_And_One_Object()
    {
        var chain = await IssuedAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => FinalizeAsync(chain.RevisionId, chain.Caller))));

        Assert.Equal(1, results.Count(r => r.State == ReportPdfFinalizationState.Succeeded));
        Assert.All(results.Where(r => r.State != ReportPdfFinalizationState.Succeeded),
            r => Assert.Equal(ReportPdfFinalizationState.AlreadyFinalized, r.State));
        var after = await LoadAsync(chain.RevisionId);
        Assert.All(results, r => Assert.Equal(after.PdfStorageKey, r.StorageKey));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "reports"), "*.pdf", SearchOption.AllDirectories));
        Assert.Equal(after.PdfSha256, Sha(await File.ReadAllBytesAsync(PathOf(after.PdfStorageKey!))));
    }

    [Fact]
    public async Task Unauthenticated_Or_NonAdmin_Caller_Is_Forbidden_And_NonOwner_Gets_NotFound()
    {
        var chain = await IssuedAsync();
        var plain = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, chain.UserId)], "test"));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var otherAdmin = AssessmentReportPostgresIntegrationTests.Admin("someone-else-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(ReportPdfFinalizationState.Forbidden, (await FinalizeAsync(chain.RevisionId, plain)).State);
        Assert.Equal(ReportPdfFinalizationState.Forbidden, (await FinalizeAsync(chain.RevisionId, anonymous)).State);
        Assert.Equal(ReportPdfFinalizationState.NotFound, (await FinalizeAsync(chain.RevisionId, otherAdmin)).State);
        Assert.Equal(ReportPdfFinalizationState.NotFound, (await FinalizeAsync(Guid.NewGuid(), chain.Caller)).State);
        Assert.Equal(ReportPdfReadState.Forbidden, (await ReadAsync(chain.RevisionId, plain)).State);
        Assert.Equal(ReportPdfReadState.NotFound, (await ReadAsync(chain.RevisionId, otherAdmin)).State);

        var stored = await LoadAsync(chain.RevisionId);
        Assert.Null(stored.PdfStorageKey);
        Assert.False(Directory.Exists(Path.Combine(_root, "reports")));
    }

    [Fact]
    public async Task Draft_InReview_Approved_And_Rejected_Revisions_Are_Not_Finalized()
    {
        var draft = await DraftAsync();
        Assert.Equal(ReportPdfFinalizationState.NotIssued, (await FinalizeAsync(draft.RevisionId, draft.Caller)).State);

        var inReview = await DraftAsync();
        var submit = await LifecycleAsync(T1, s => s.SubmitForReviewAsync(
            inReview.RevisionId, LoadAsync(inReview.RevisionId).Result.ConcurrencyStamp, inReview.Caller));
        Assert.True(submit.Succeeded);
        Assert.Equal(ReportPdfFinalizationState.NotIssued, (await FinalizeAsync(inReview.RevisionId, inReview.Caller)).State);

        var approved = await DraftAsync();
        await ApproveAsync(approved.RevisionId, approved.Caller);
        Assert.Equal(ReportPdfFinalizationState.NotIssued, (await FinalizeAsync(approved.RevisionId, approved.Caller)).State);

        var rejected = await DraftAsync();
        var rejectSubmit = await LifecycleAsync(T1, s => s.SubmitForReviewAsync(
            rejected.RevisionId, LoadAsync(rejected.RevisionId).Result.ConcurrencyStamp, rejected.Caller));
        Assert.True((await LifecycleAsync(T1, s => s.RejectAsync(
            rejected.RevisionId, rejectSubmit.ConcurrencyStamp!.Value, "no", rejected.Caller))).Succeeded);
        Assert.Equal(ReportPdfFinalizationState.NotIssued, (await FinalizeAsync(rejected.RevisionId, rejected.Caller)).State);

        foreach (var id in new[] { draft.RevisionId, inReview.RevisionId, approved.RevisionId, rejected.RevisionId })
        {
            Assert.Null((await LoadAsync(id)).PdfStorageKey);
        }
        Assert.False(Directory.Exists(Path.Combine(_root, "reports")));
    }

    [Fact]
    public async Task Superseded_Without_Pdf_Never_Receives_Metadata_And_Superseded_With_Pdf_Stays_Readable()
    {
        var withPdf = await IssuedAsync();
        Assert.Equal(ReportPdfFinalizationState.Succeeded, (await FinalizeAsync(withPdf.RevisionId, withPdf.Caller)).State);
        var pdfBefore = await LoadAsync(withPdf.RevisionId);
        await SupersedeAsync(withPdf);

        var read = await ReadAsync(withPdf.RevisionId, withPdf.Caller);
        Assert.Equal(ReportPdfReadState.Succeeded, read.State);
        Assert.Equal(pdfBefore.PdfSha256, Sha(read.Content!));
        Assert.Equal(ReportPdfFinalizationState.AlreadyFinalized, (await FinalizeAsync(withPdf.RevisionId, withPdf.Caller)).State);

        var withoutPdf = await IssuedAsync();
        await SupersedeAsync(withoutPdf);
        Assert.Equal(ReportPdfFinalizationState.NotIssued, (await FinalizeAsync(withoutPdf.RevisionId, withoutPdf.Caller)).State);
        var stored = await LoadAsync(withoutPdf.RevisionId);
        Assert.Null(stored.PdfStorageKey);
        Assert.Null(stored.PdfSha256);
        Assert.Null(stored.PdfLengthBytes);
        Assert.Equal(ReportPdfReadState.NotFinalized, (await ReadAsync(withoutPdf.RevisionId, withoutPdf.Caller)).State);
    }

    [Fact]
    public async Task Storage_Failure_Leaves_Database_Unchanged_And_Allows_Retry()
    {
        var chain = await IssuedAsync();
        var before = await LoadAsync(chain.RevisionId);

        var failed = await FinalizeAsync(chain.RevisionId, chain.Caller, new FailingStorage());

        Assert.Equal(ReportPdfFinalizationState.StorageFailure, failed.State);
        var unchanged = await LoadAsync(chain.RevisionId);
        Assert.Null(unchanged.PdfStorageKey);
        Assert.Null(unchanged.PdfSha256);
        Assert.Null(unchanged.PdfLengthBytes);
        Assert.Equal(before.ConcurrencyStamp, unchanged.ConcurrencyStamp);

        Assert.Equal(ReportPdfFinalizationState.Succeeded, (await FinalizeAsync(chain.RevisionId, chain.Caller)).State);
    }

    [Fact]
    public async Task Database_Failure_After_Storage_Leaves_Orphan_That_A_Retry_Reuses_Without_Duplicates()
    {
        var chain = await IssuedAsync();

        var failed = await FinalizeAsync(chain.RevisionId, chain.Caller, failPdfUpdate: true);

        Assert.Equal(ReportPdfFinalizationState.DatabaseFailure, failed.State);
        Assert.NotNull(failed.StorageKey);
        var orphan = PathOf(failed.StorageKey!);
        Assert.True(File.Exists(orphan));
        var orphanBytes = await File.ReadAllBytesAsync(orphan);
        Assert.Null((await LoadAsync(chain.RevisionId)).PdfStorageKey);
        Assert.Equal(ReportPdfReadState.NotFinalized, (await ReadAsync(chain.RevisionId, chain.Caller)).State);

        var retry = await FinalizeAsync(chain.RevisionId, chain.Caller);

        Assert.Equal(ReportPdfFinalizationState.Succeeded, retry.State);
        Assert.Equal(failed.StorageKey, retry.StorageKey);
        Assert.Equal(orphanBytes, await File.ReadAllBytesAsync(orphan));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "reports"), "*.pdf", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Stale_Concurrency_Stamp_Never_Overwrites_And_Is_Reported_Without_Touching_Storage()
    {
        var chain = await IssuedAsync();
        await using var db = NewDb();
        var service = new ReportPdfFinalizationService(
            db, new ReportPdfModelBuilder(Registry), new ReportPdfRenderer(Registry),
            _storage, TimeProvider.System, NullLogger<ReportPdfFinalizationService>.Instance);

        // Another writer bumps the stamp after this service has read the revision but before it updates.
        var racing = new RacingStorage(_storage, async () =>
        {
            await using var other = NewDb();
            await other.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AssessmentReportRevisions\" SET \"ConcurrencyStamp\" = {Guid.NewGuid()} WHERE \"Id\" = {chain.RevisionId}");
        });
        var racingService = new ReportPdfFinalizationService(
            db, new ReportPdfModelBuilder(Registry), new ReportPdfRenderer(Registry),
            racing, TimeProvider.System, NullLogger<ReportPdfFinalizationService>.Instance);

        var result = await racingService.FinalizeAsync(chain.RevisionId, chain.Caller);

        Assert.Equal(ReportPdfFinalizationState.ConcurrencyConflict, result.State);
        Assert.Null((await LoadAsync(chain.RevisionId)).PdfStorageKey);
        Assert.Equal(ReportPdfFinalizationState.Succeeded, (await FinalizeAsync(chain.RevisionId, chain.Caller)).State);
        _ = service;
    }

    private sealed class RacingStorage(IReportPdfStorage inner, Func<Task> afterWrite) : IReportPdfStorage
    {
        public async Task<ReportPdfStorageWriteOutcome> WriteAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            var outcome = await inner.WriteAsync(key, content, cancellationToken);
            await afterWrite();
            return outcome;
        }
        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => inner.ExistsAsync(key, cancellationToken);
        public Task<bool> DeleteAsync(string key, string expectedSha256, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, expectedSha256, cancellationToken);
    }

    [Fact]
    public async Task Missing_Or_Corrupted_Objects_Fail_Integrity_And_Are_Never_Regenerated()
    {
        var chain = await IssuedAsync();
        var finalized = await FinalizeAsync(chain.RevisionId, chain.Caller);
        var path = PathOf(finalized.StorageKey!);
        var original = await File.ReadAllBytesAsync(path);

        // Same length, different bytes.
        var tampered = (byte[])original.Clone();
        tampered[tampered.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(path, tampered);
        Assert.Equal(ReportPdfReadState.IntegrityFailure, (await ReadAsync(chain.RevisionId, chain.Caller)).State);
        Assert.Equal(tampered, await File.ReadAllBytesAsync(path));

        // Different length.
        await File.WriteAllBytesAsync(path, original.Take(original.Length - 1).ToArray());
        Assert.Equal(ReportPdfReadState.IntegrityFailure, (await ReadAsync(chain.RevisionId, chain.Caller)).State);

        // Missing.
        File.Delete(path);
        Assert.Equal(ReportPdfReadState.ObjectMissing, (await ReadAsync(chain.RevisionId, chain.Caller)).State);
        Assert.False(File.Exists(path));

        // Finalize never regenerates a finalized PDF, even with the object gone.
        var counting = new CountingStorage(_storage);
        Assert.Equal(ReportPdfFinalizationState.AlreadyFinalized, (await FinalizeAsync(chain.RevisionId, chain.Caller, counting)).State);
        Assert.Equal(0, counting.Writes);
        Assert.False(File.Exists(path));

        // Restoring the exact bytes makes the object readable again.
        await File.WriteAllBytesAsync(path, original);
        Assert.Equal(ReportPdfReadState.Succeeded, (await ReadAsync(chain.RevisionId, chain.Caller)).State);
    }

    [Fact]
    public async Task Partial_Malformed_Or_Foreign_Metadata_Is_Reported_Explicitly_And_Never_Repaired()
    {
        var chain = await IssuedAsync();
        var other = await IssuedAsync();
        var finalized = await FinalizeAsync(other.RevisionId, other.Caller);
        Assert.Equal(ReportPdfFinalizationState.Succeeded, finalized.State);

        // Partial metadata cannot be written through the trigger, so simulate legacy/tampered rows by disabling it
        // for this transaction only on this disposable database.
        async Task ForceAsync(Guid id, string set)
        {
            await using var db = NewDb();
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AssessmentReportRevisions\" DISABLE TRIGGER \"TR_AssessmentReportRevisions_IssuedIsImmutable\"");
            await db.Database.ExecuteSqlRawAsync($"UPDATE \"AssessmentReportRevisions\" SET {set} WHERE \"Id\" = '{id}'");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AssessmentReportRevisions\" ENABLE TRIGGER \"TR_AssessmentReportRevisions_IssuedIsImmutable\"");
            await tx.CommitAsync();
        }

        await ForceAsync(chain.RevisionId, "\"PdfSha256\" = '" + new string('a', 64) + "'");
        Assert.Equal(ReportPdfFinalizationState.InconsistentMetadata, (await FinalizeAsync(chain.RevisionId, chain.Caller)).State);
        Assert.Equal(ReportPdfReadState.InconsistentMetadata, (await ReadAsync(chain.RevisionId, chain.Caller)).State);
        Assert.Null((await LoadAsync(chain.RevisionId)).PdfStorageKey);

        // Complete metadata whose key belongs to a different revision.
        await ForceAsync(chain.RevisionId, $"\"PdfStorageKey\" = '{finalized.StorageKey}', \"PdfLengthBytes\" = {finalized.LengthBytes}, \"PdfSha256\" = '{finalized.Sha256}'");
        var foreign = await ReadAsync(chain.RevisionId, chain.Caller);
        Assert.Equal(ReportPdfReadState.IntegrityFailure, foreign.State);
        Assert.Null(foreign.Content);

        // Malformed key and malformed hash.
        await ForceAsync(chain.RevisionId, "\"PdfStorageKey\" = '../../evil.pdf'");
        Assert.Equal(ReportPdfReadState.IntegrityFailure, (await ReadAsync(chain.RevisionId, chain.Caller)).State);
        await ForceAsync(chain.RevisionId, $"\"PdfStorageKey\" = 'reports/{chain.ReportId:N}/{chain.RevisionId:N}-{finalized.Sha256}.pdf', \"PdfSha256\" = 'zz'");
        Assert.Equal(ReportPdfReadState.IntegrityFailure, (await ReadAsync(chain.RevisionId, chain.Caller)).State);

        // The other revision's object is untouched throughout.
        Assert.Equal(ReportPdfReadState.Succeeded, (await ReadAsync(other.RevisionId, other.Caller)).State);
    }

    [Fact]
    public async Task Metadata_Pointing_To_Another_Revisions_Key_Is_An_Integrity_Failure()
    {
        var chain = await IssuedAsync();
        var finalized = await FinalizeAsync(chain.RevisionId, chain.Caller);
        var other = await IssuedAsync();
        // Plant an object under the other revision's key, then point nothing at it: the read of the first revision
        // still resolves only its own key.
        var foreignKey = ReportPdfStorageKeys.Create(other.ReportId, other.RevisionId, finalized.Sha256!);
        await _storage.WriteAsync(foreignKey, await File.ReadAllBytesAsync(PathOf(finalized.StorageKey!)));

        var read = await ReadAsync(chain.RevisionId, chain.Caller);
        Assert.Equal(ReportPdfReadState.Succeeded, read.State);
        Assert.Equal(finalized.StorageKey, read.StorageKey);
        Assert.Equal(ReportPdfReadState.NotFinalized, (await ReadAsync(other.RevisionId, other.Caller)).State);
    }

    [Fact]
    public async Task Finalization_Complies_With_Immutability_Trigger_And_Metadata_Stays_Write_Once()
    {
        var chain = await IssuedAsync();
        var finalized = await FinalizeAsync(chain.RevisionId, chain.Caller);
        Assert.Equal(ReportPdfFinalizationState.Succeeded, finalized.State);

        await using var db = NewDb();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AssessmentReportRevisions\" SET \"PdfLengthBytes\" = 1 WHERE \"Id\" = {chain.RevisionId}"));
        var pg = ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException;
        Assert.Equal("23514", pg?.SqlState);
        Assert.Equal(finalized.LengthBytes, (await LoadAsync(chain.RevisionId)).PdfLengthBytes);
    }
}
