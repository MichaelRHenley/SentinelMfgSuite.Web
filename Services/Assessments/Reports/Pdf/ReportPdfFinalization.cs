using System.Security.Claims;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;

public enum ReportPdfFinalizationState
{
    /// <summary>The PDF was rendered, stored and its metadata finalized by this request.</summary>
    Succeeded = 0,

    /// <summary>PDF metadata was already finalized (possibly by a concurrent request). Nothing was regenerated.</summary>
    AlreadyFinalized = 1,

    Forbidden = 2,
    NotFound = 3,

    /// <summary>The revision is not Issued (Draft, InReview, Approved, Rejected, or Superseded without a PDF).</summary>
    NotIssued = 4,

    /// <summary>The frozen revision data could not be turned into a render model.</summary>
    InvalidSourceData = 5,

    TemplateUnsupported = 6,
    RenderFailure = 7,
    StorageFailure = 8,

    /// <summary>Storage succeeded but the metadata update failed. The stored object is an unreferenced orphan.</summary>
    DatabaseFailure = 9,

    /// <summary>The revision changed concurrently and is not finalized; retry with fresh state.</summary>
    ConcurrencyConflict = 10,

    /// <summary>Only some PDF metadata fields are set. Requires operator attention; nothing was changed.</summary>
    InconsistentMetadata = 11
}

public sealed record ReportPdfFinalizationResult(
    ReportPdfFinalizationState State,
    Guid? RevisionId = null,
    string? StorageKey = null,
    string? Sha256 = null,
    long? LengthBytes = null,
    Guid? ConcurrencyStamp = null,
    IReadOnlyList<string>? ErrorCodes = null)
{
    public bool Succeeded => State == ReportPdfFinalizationState.Succeeded;

    public bool HasFinalizedPdf => State is ReportPdfFinalizationState.Succeeded or ReportPdfFinalizationState.AlreadyFinalized;
}

public enum ReportPdfReadState
{
    Succeeded = 0,
    Forbidden = 1,
    NotFound = 2,

    /// <summary>The revision has no finalized PDF metadata (or is not Issued/Superseded).</summary>
    NotFinalized = 3,

    /// <summary>Metadata exists but the storage object is missing.</summary>
    ObjectMissing = 4,

    /// <summary>The stored length or SHA-256 does not match the finalized metadata, or the key is invalid.</summary>
    IntegrityFailure = 5,

    StorageFailure = 6,
    InconsistentMetadata = 7
}

public sealed record ReportPdfReadResult(
    ReportPdfReadState State,
    byte[]? Content = null,
    string? StorageKey = null,
    string? Sha256 = null,
    long? LengthBytes = null)
{
    public bool Succeeded => State == ReportPdfReadState.Succeeded;
}

public interface IReportPdfFinalizationService
{
    /// <summary>
    /// Renders the Issued revision from its frozen data, stores the PDF privately and finalizes
    /// PdfStorageKey/PdfSha256/PdfLengthBytes exactly once. Never regenerates a finalized PDF.
    /// </summary>
    Task<ReportPdfFinalizationResult> FinalizeAsync(Guid revisionId, ClaimsPrincipal caller, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a finalized PDF (Issued or Superseded revision) after verifying object existence, length and SHA-256.
    /// Never regenerates or replaces anything.
    /// </summary>
    Task<ReportPdfReadResult> ReadAsync(Guid revisionId, ClaimsPrincipal caller, CancellationToken cancellationToken = default);
}
