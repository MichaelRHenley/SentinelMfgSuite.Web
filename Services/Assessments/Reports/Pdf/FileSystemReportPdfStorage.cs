using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;

public static class ReportPdfStorageConfiguration
{
    public const string RootPathSetting = "ReportPdfStorage:RootPath";

    /// <summary>
    /// Resolves the storage root. It must be an absolute path outside the application content root (and therefore
    /// outside wwwroot). Only Development may omit it, falling back to a per-user application-data directory.
    /// </summary>
    public static string ResolveRoot(string? configuredRoot, IHostEnvironment environment)
    {
        var root = configuredRoot?.Trim();
        if (string.IsNullOrEmpty(root))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"'{RootPathSetting}' must be configured with an absolute directory outside the application directory.");
            }

            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SentinelMfgSuite", "report-pdfs");
        }

        if (!Path.IsPathRooted(root))
        {
            throw new InvalidOperationException($"'{RootPathSetting}' must be an absolute path.");
        }

        var full = Path.GetFullPath(root);
        var contentRoot = Path.GetFullPath(environment.ContentRootPath);
        if (FileSystemReportPdfStorage.IsUnder(full, contentRoot) || FileSystemReportPdfStorage.IsUnder(contentRoot, full))
        {
            throw new InvalidOperationException(
                $"'{RootPathSetting}' must not be inside (or contain) the application content root, which may be publicly served or replaced on deploy.");
        }

        return full;
    }
}

/// <summary>
/// Private filesystem implementation of <see cref="IReportPdfStorage"/>. Objects are written to a temporary file and
/// published with a non-overwriting move, so a reader never observes a partial object and two concurrent writers can
/// never replace each other's content.
/// </summary>
public sealed class FileSystemReportPdfStorage : IReportPdfStorage
{
    private const string TempDirectoryName = ".tmp";

    private readonly string _root;

    public FileSystemReportPdfStorage(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathRooted(rootPath))
        {
            throw new ArgumentException("The storage root must be an absolute path.", nameof(rootPath));
        }

        _root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static bool IsUnder(string path, string directory)
    {
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return (path + Path.DirectorySeparatorChar).StartsWith(prefix, comparison);
    }

    private string ResolvePath(string key)
    {
        if (!ReportPdfStorageKeys.IsValid(key))
        {
            throw new ArgumentException("The storage key is not valid.", nameof(key));
        }

        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnder(full, _root))
        {
            throw new ArgumentException("The storage key resolves outside the storage root.", nameof(key));
        }

        EnsureNoLinks(Path.GetRelativePath(_root, full));
        return full;
    }

    /// <summary>
    /// Rejects any existing component beneath the root (including the temp directory and the object itself) that is a
    /// symbolic link, junction or other reparse point, so file operations cannot be redirected outside the root.
    /// The root itself may be a mount or link chosen by the operator.
    /// </summary>
    private void EnsureNoLinks(string relativePath)
    {
        var current = _root;
        foreach (var part in relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                throw new ReportPdfStorageException("The storage path contains a link or reparse point and was refused.");
            }
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public async Task<ReportPdfStorageWriteOutcome> WriteAsync(
        string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);
        if (content.IsEmpty)
        {
            throw new ArgumentException("Empty content cannot be stored.", nameof(content));
        }

        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            EnsureNoLinks(Path.GetRelativePath(_root, Path.GetDirectoryName(path)!));
            var tempDirectory = Path.Combine(_root, TempDirectoryName);
            Directory.CreateDirectory(tempDirectory);
            EnsureNoLinks(TempDirectoryName);
            temp = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".tmp");

            await using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temp, path, overwrite: false);
                temp = null;
                return ReportPdfStorageWriteOutcome.Created;
            }
            catch (IOException) when (File.Exists(path))
            {
                var existing = await File.ReadAllBytesAsync(path, cancellationToken);
                if (existing.AsSpan().SequenceEqual(content.Span))
                {
                    return ReportPdfStorageWriteOutcome.AlreadyExistsIdentical;
                }

                throw new ReportPdfStorageConflictException(
                    "A different object already exists under this storage key; it was not overwritten.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReportPdfStorageException("The PDF could not be written to private storage.", ex);
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); } catch (Exception) { /* best effort; temp files are inert and under .tmp */ }
            }
        }
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);
        try
        {
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReportPdfStorageException("The PDF could not be read from private storage.", ex);
        }
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(File.Exists(ResolvePath(key)));

    public async Task<bool> DeleteAsync(string key, string expectedSha256, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            throw new ArgumentException("The expected SHA-256 is required for deletion.", nameof(expectedSha256));
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (!string.Equals(Hash(bytes), expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new ReportPdfStorageConflictException("The stored object does not match the expected SHA-256; it was not deleted.");
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReportPdfStorageException("The PDF could not be deleted from private storage.", ex);
        }
    }
}
