using System.Text.RegularExpressions;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;

public enum ReportPdfStorageWriteOutcome
{
    /// <summary>The object did not exist and was created.</summary>
    Created = 0,

    /// <summary>An object with this key already existed and its bytes are identical. Nothing was changed.</summary>
    AlreadyExistsIdentical = 1
}

/// <summary>An infrastructure failure while accessing private PDF storage.</summary>
public class ReportPdfStorageException : Exception
{
    public ReportPdfStorageException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>An object already exists under the key but holds different bytes. It is never overwritten.</summary>
public sealed class ReportPdfStorageConflictException : ReportPdfStorageException
{
    public ReportPdfStorageConflictException(string message) : base(message) { }
}

/// <summary>
/// Private, write-once storage for issued report PDFs. Keys are opaque, validated strings; no operation here
/// produces a public URL.
/// </summary>
public interface IReportPdfStorage
{
    /// <summary>
    /// Creates the object atomically. Never overwrites: identical existing content is reported as
    /// <see cref="ReportPdfStorageWriteOutcome.AlreadyExistsIdentical"/>, different content throws
    /// <see cref="ReportPdfStorageConflictException"/>.
    /// </summary>
    Task<ReportPdfStorageWriteOutcome> WriteAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Returns the object bytes, or null when no object exists.</summary>
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Controlled deletion for operator-driven reconciliation only. Deletes only when the stored bytes hash to
    /// <paramref name="expectedSha256"/>. Returns false when the object does not exist; throws
    /// <see cref="ReportPdfStorageConflictException"/> when the hash does not match.
    /// </summary>
    Task<bool> DeleteAsync(string key, string expectedSha256, CancellationToken cancellationToken = default);
}

/// <summary>
/// Storage key format: <c>reports/{reportId:N}/{revisionId:N}-{sha256}.pdf</c>. Revision-scoped (so an object is never
/// shared between revisions) and content-addressed (so different bytes can never collide on a key).
/// </summary>
public static partial class ReportPdfStorageKeys
{
    [GeneratedRegex("^reports/[0-9a-f]{32}/[0-9a-f]{32}-[0-9a-f]{64}\\.pdf$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public static string Create(Guid reportId, Guid revisionId, string sha256Lower)
    {
        if (sha256Lower is null || sha256Lower.Length != 64 || !sha256Lower.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("SHA-256 must be 64 lowercase hexadecimal characters.", nameof(sha256Lower));
        }

        return $"reports/{reportId:N}/{revisionId:N}-{sha256Lower}.pdf";
    }

    public static bool IsValid(string? key) => key is not null && KeyPattern().IsMatch(key);
}
