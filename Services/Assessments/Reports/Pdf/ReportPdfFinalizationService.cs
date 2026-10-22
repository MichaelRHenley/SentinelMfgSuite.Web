using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;

/// <summary>
/// Sequence: authorize -> load revision (no tracking) -> require Issued -> short-circuit if already finalized ->
/// build model from frozen data -> render -> SHA-256/length -> write-once private storage -> guarded metadata update.
/// The metadata update is a single conditional UPDATE (id, Issued, all three PDF fields null, expected stamp) that
/// sets the three fields together, so the PostgreSQL immutability trigger sees one complete write-once change.
/// A stored object without metadata is an orphan: it is content-addressed and revision-scoped, never deleted
/// automatically, and an identical retry simply reuses it.
/// </summary>
public sealed class ReportPdfFinalizationService : IReportPdfFinalizationService
{
    private readonly ApplicationDbContext _db;
    private readonly IReportPdfModelBuilder _builder;
    private readonly IReportPdfRenderer _renderer;
    private readonly IReportPdfStorage _storage;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReportPdfFinalizationService> _logger;

    public ReportPdfFinalizationService(
        ApplicationDbContext db,
        IReportPdfModelBuilder builder,
        IReportPdfRenderer renderer,
        IReportPdfStorage storage,
        TimeProvider timeProvider,
        ILogger<ReportPdfFinalizationService> logger)
    {
        _db = db;
        _builder = builder;
        _renderer = renderer;
        _storage = storage;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private static int SetCount(AssessmentReportRevision r)
        => (r.PdfStorageKey is not null ? 1 : 0) + (r.PdfSha256 is not null ? 1 : 0) + (r.PdfLengthBytes is not null ? 1 : 0);

    private static ReportPdfFinalizationResult Existing(ReportPdfFinalizationState state, AssessmentReportRevision r)
        => new(state, r.Id, r.PdfStorageKey, r.PdfSha256, r.PdfLengthBytes, r.ConcurrencyStamp);

    public async Task<ReportPdfFinalizationResult> FinalizeAsync(
        Guid revisionId, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        if (!AssessmentReportAccess.TryGetCaller(caller, out var actorId))
        {
            return new(ReportPdfFinalizationState.Forbidden);
        }

        var revision = await AssessmentReportAccess.LoadOwnedRevisionAsync(_db, revisionId, actorId, false, cancellationToken);
        if (revision is null)
        {
            return new(ReportPdfFinalizationState.NotFound);
        }

        var set = SetCount(revision);
        if (set == 3)
        {
            return Existing(ReportPdfFinalizationState.AlreadyFinalized, revision);
        }
        if (set != 0)
        {
            return new(ReportPdfFinalizationState.InconsistentMetadata, revision.Id);
        }
        if (revision.Status != AssessmentReportRevisionStatus.Issued)
        {
            // Includes Superseded without a PDF: it never receives new PDF metadata.
            return new(ReportPdfFinalizationState.NotIssued, revision.Id);
        }

        var report = revision.AssessmentReport;
        var build = _builder.Build(new ReportPdfSource(
            report, revision, report.Assessment.Title, report.Assessment.SiteName));
        if (!build.Succeeded)
        {
            var codes = build.Errors.Select(e => e.Code).ToList();
            return new(
                codes.Contains("template-unsupported")
                    ? ReportPdfFinalizationState.TemplateUnsupported
                    : ReportPdfFinalizationState.InvalidSourceData,
                revision.Id, ErrorCodes: codes);
        }

        byte[] pdf;
        try
        {
            pdf = _renderer.Render(build.Model!);
        }
        catch (ReportPdfTemplateUnsupportedException)
        {
            return new(ReportPdfFinalizationState.TemplateUnsupported, revision.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Rendering the PDF for report revision {RevisionId} failed.", revision.Id);
            return new(ReportPdfFinalizationState.RenderFailure, revision.Id);
        }

        if (pdf is null || pdf.Length == 0)
        {
            return new(ReportPdfFinalizationState.RenderFailure, revision.Id);
        }

        var sha = Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant();
        var length = (long)pdf.Length;
        var key = ReportPdfStorageKeys.Create(report.Id, revision.Id, sha);

        try
        {
            await _storage.WriteAsync(key, pdf, cancellationToken);
        }
        catch (Exception ex) when (ex is ReportPdfStorageException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Storing the PDF for report revision {RevisionId} failed.", revision.Id);
            return new(ReportPdfFinalizationState.StorageFailure, revision.Id);
        }

        var expectedStamp = revision.ConcurrencyStamp;
        var newStamp = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        int updated;
        try
        {
            updated = await _db.Set<AssessmentReportRevision>()
                .Where(item => item.Id == revision.Id
                    && item.Status == AssessmentReportRevisionStatus.Issued
                    && item.PdfStorageKey == null
                    && item.PdfSha256 == null
                    && item.PdfLengthBytes == null
                    && item.ConcurrencyStamp == expectedStamp)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.PdfStorageKey, key)
                    .SetProperty(item => item.PdfSha256, sha)
                    .SetProperty(item => item.PdfLengthBytes, length)
                    .SetProperty(item => item.ConcurrencyStamp, newStamp)
                    .SetProperty(item => item.UpdatedUtc, now)
                    .SetProperty(item => item.UpdatedByUserId, actorId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Finalizing PDF metadata for report revision {RevisionId} failed after storing object {StorageKey}; the object is an unreferenced orphan.",
                revision.Id, key);
            return new(ReportPdfFinalizationState.DatabaseFailure, revision.Id, key, sha, length);
        }

        if (updated == 1)
        {
            return new(ReportPdfFinalizationState.Succeeded, revision.Id, key, sha, length, newStamp);
        }

        // The guard rejected the update: classify against current committed state. Never touch the stored object,
        // which may belong to a concurrent successful request.
        var current = await _db.Set<AssessmentReportRevision>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == revision.Id, cancellationToken);
        if (current is null)
        {
            return new(ReportPdfFinalizationState.NotFound);
        }
        if (SetCount(current) == 3)
        {
            return Existing(ReportPdfFinalizationState.AlreadyFinalized, current);
        }
        if (current.Status != AssessmentReportRevisionStatus.Issued)
        {
            return new(ReportPdfFinalizationState.NotIssued, current.Id);
        }
        return new(ReportPdfFinalizationState.ConcurrencyConflict, current.Id, ConcurrencyStamp: current.ConcurrencyStamp);
    }

    public async Task<ReportPdfReadResult> ReadAsync(
        Guid revisionId, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        if (!AssessmentReportAccess.TryGetCaller(caller, out var actorId))
        {
            return new(ReportPdfReadState.Forbidden);
        }

        var revision = await AssessmentReportAccess.LoadOwnedRevisionAsync(_db, revisionId, actorId, false, cancellationToken);
        if (revision is null)
        {
            return new(ReportPdfReadState.NotFound);
        }

        var set = SetCount(revision);
        if (set == 0
            || revision.Status is not (AssessmentReportRevisionStatus.Issued or AssessmentReportRevisionStatus.Superseded))
        {
            return new(ReportPdfReadState.NotFinalized);
        }
        if (set != 3)
        {
            return new(ReportPdfReadState.InconsistentMetadata);
        }

        var key = revision.PdfStorageKey!;
        var expectedPrefix = $"reports/{revision.AssessmentReportId:N}/{revision.Id:N}-";
        if (!ReportPdfStorageKeys.IsValid(key)
            || !key.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || !key.EndsWith($"-{revision.PdfSha256!.ToLowerInvariant()}.pdf", StringComparison.Ordinal))
        {
            return new(ReportPdfReadState.IntegrityFailure, StorageKey: key);
        }

        byte[]? bytes;
        try
        {
            bytes = await _storage.ReadAsync(key, cancellationToken);
        }
        catch (ArgumentException)
        {
            return new(ReportPdfReadState.IntegrityFailure, StorageKey: key);
        }
        catch (ReportPdfStorageException ex)
        {
            _logger.LogError(ex, "Reading the PDF for report revision {RevisionId} failed.", revision.Id);
            return new(ReportPdfReadState.StorageFailure, StorageKey: key);
        }

        if (bytes is null)
        {
            return new(ReportPdfReadState.ObjectMissing, StorageKey: key);
        }

        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.LongLength != revision.PdfLengthBytes
            || !string.Equals(sha, revision.PdfSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("Stored PDF for report revision {RevisionId} failed integrity verification.", revision.Id);
            return new(ReportPdfReadState.IntegrityFailure, StorageKey: key);
        }

        return new(ReportPdfReadState.Succeeded, bytes, key, revision.PdfSha256, revision.PdfLengthBytes);
    }
}
