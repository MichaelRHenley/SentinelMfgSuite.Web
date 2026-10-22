using Xunit;
using SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;

namespace SentinelMfgSuite.Web.Tests;

public sealed class FileSystemReportPdfStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sentinel-pdf-store-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemReportPdfStorage _storage;
    private static readonly Guid ReportId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RevisionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public FileSystemReportPdfStorageTests() => _storage = new FileSystemReportPdfStorage(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static string Key(string sha) => ReportPdfStorageKeys.Create(ReportId, RevisionId, sha);

    private static string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Write_Read_Exists_RoundTrip()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var key = Key(Sha(bytes));

        Assert.False(await _storage.ExistsAsync(key));
        Assert.Null(await _storage.ReadAsync(key));
        Assert.Equal(ReportPdfStorageWriteOutcome.Created, await _storage.WriteAsync(key, bytes));
        Assert.True(await _storage.ExistsAsync(key));
        Assert.Equal(bytes, await _storage.ReadAsync(key));
    }

    [Fact]
    public async Task Identical_Rewrite_Is_Idempotent_And_Different_Content_Never_Overwrites()
    {
        var bytes = new byte[] { 9, 9, 9 };
        var key = Key(Sha(bytes));
        await _storage.WriteAsync(key, bytes);

        Assert.Equal(ReportPdfStorageWriteOutcome.AlreadyExistsIdentical, await _storage.WriteAsync(key, bytes));
        await Assert.ThrowsAsync<ReportPdfStorageConflictException>(() => _storage.WriteAsync(key, new byte[] { 1 }));
        Assert.Equal(bytes, await _storage.ReadAsync(key));
    }

    [Fact]
    public async Task Concurrent_Writers_Of_Same_Content_All_Succeed_With_One_Object_And_No_Temp_Leftovers()
    {
        var bytes = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        var key = Key(Sha(bytes));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => _storage.WriteAsync(key, bytes))));

        Assert.Equal(1, outcomes.Count(o => o == ReportPdfStorageWriteOutcome.Created));
        Assert.Equal(11, outcomes.Count(o => o == ReportPdfStorageWriteOutcome.AlreadyExistsIdentical));
        Assert.Equal(bytes, await _storage.ReadAsync(key));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".tmp")));
    }

    [Theory]
    [InlineData("../evil.pdf")]
    [InlineData("reports/../../evil.pdf")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("reports\\11111111111111111111111111111111\\33333333333333333333333333333333-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.pdf")]
    [InlineData("reports/11111111111111111111111111111111/33333333333333333333333333333333-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.pdf")]
    [InlineData("reports/x/y.pdf")]
    [InlineData("")]
    public async Task Invalid_Or_Traversal_Keys_Are_Rejected(string key)
    {
        Assert.False(ReportPdfStorageKeys.IsValid(key));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.WriteAsync(key, new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.ReadAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.ExistsAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.DeleteAsync(key, new string('a', 64)));
    }

    [Fact]
    public async Task Empty_Content_Is_Rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.WriteAsync(Key(new string('a', 64)), ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public async Task Delete_Requires_Matching_Hash()
    {
        var bytes = new byte[] { 5, 6, 7 };
        var key = Key(Sha(bytes));
        await _storage.WriteAsync(key, bytes);

        await Assert.ThrowsAsync<ReportPdfStorageConflictException>(() => _storage.DeleteAsync(key, new string('0', 64)));
        Assert.True(await _storage.ExistsAsync(key));

        Assert.True(await _storage.DeleteAsync(key, Sha(bytes).ToUpperInvariant()));
        Assert.False(await _storage.ExistsAsync(key));
        Assert.False(await _storage.DeleteAsync(key, Sha(bytes)));
    }

    [Fact]
    public void Key_Create_Validates_Hash_Format()
    {
        Assert.Throws<ArgumentException>(() => ReportPdfStorageKeys.Create(ReportId, RevisionId, "ABC"));
        Assert.Throws<ArgumentException>(() => ReportPdfStorageKeys.Create(ReportId, RevisionId, new string('A', 64)));
        Assert.True(ReportPdfStorageKeys.IsValid(ReportPdfStorageKeys.Create(ReportId, RevisionId, new string('a', 64))));
    }

    private bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false; // symlink creation needs privilege on some Windows hosts; nothing to assert then
        }
    }

    [Fact]
    public async Task Symlinked_Directory_Inside_Root_Is_Refused_And_Nothing_Is_Written_Outside()
    {
        var outside = Path.Combine(Path.GetTempPath(), "sentinel-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateDirectory(Path.Combine(_root, "reports"));
            if (!TryLink(Path.Combine(_root, "reports", ReportId.ToString("N")), outside))
            {
                return;
            }

            var bytes = new byte[] { 1, 2, 3 };
            var key = Key(Sha(bytes));
            await Assert.ThrowsAsync<ReportPdfStorageException>(() => _storage.WriteAsync(key, bytes));
            await Assert.ThrowsAsync<ReportPdfStorageException>(() => _storage.ReadAsync(key));
            await Assert.ThrowsAsync<ReportPdfStorageException>(() => _storage.ExistsAsync(key));
            await Assert.ThrowsAsync<ReportPdfStorageException>(() => _storage.DeleteAsync(key, Sha(bytes)));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task Symlinked_Temp_Directory_Is_Refused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "sentinel-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateDirectory(_root);
            if (!TryLink(Path.Combine(_root, ".tmp"), outside))
            {
                return;
            }

            var bytes = new byte[] { 4, 5, 6 };
            await Assert.ThrowsAsync<ReportPdfStorageException>(() => _storage.WriteAsync(Key(Sha(bytes)), bytes));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task Temp_Files_Are_Not_Visible_As_Objects()
    {
        var bytes = new byte[] { 7, 8, 9 };
        var key = Key(Sha(bytes));
        Directory.CreateDirectory(Path.Combine(_root, ".tmp"));
        await File.WriteAllBytesAsync(Path.Combine(_root, ".tmp", "partial.tmp"), bytes);

        Assert.False(await _storage.ExistsAsync(key));
        Assert.Null(await _storage.ReadAsync(key));
        Assert.False(ReportPdfStorageKeys.IsValid(".tmp/partial.tmp"));
    }

    [Fact]
    public void Relative_Root_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() => new FileSystemReportPdfStorage("relative/path"));
    }

    private sealed class Env(string contentRoot, string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void Configuration_Requires_Root_Outside_Content_Root_And_Outside_Development()
    {
        var content = Path.Combine(Path.GetTempPath(), "app-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidOperationException>(() => ReportPdfStorageConfiguration.ResolveRoot(null, new Env(content, "Production")));
        Assert.Throws<InvalidOperationException>(() => ReportPdfStorageConfiguration.ResolveRoot("relative", new Env(content, "Production")));
        Assert.Throws<InvalidOperationException>(() => ReportPdfStorageConfiguration.ResolveRoot(Path.Combine(content, "wwwroot", "pdf"), new Env(content, "Production")));
        Assert.Throws<InvalidOperationException>(() => ReportPdfStorageConfiguration.ResolveRoot(Path.GetTempPath(), new Env(content, "Production")));

        var ok = Path.Combine(Path.GetTempPath(), "store-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(Path.GetFullPath(ok), ReportPdfStorageConfiguration.ResolveRoot(ok, new Env(content, "Production")));
        Assert.False(string.IsNullOrEmpty(ReportPdfStorageConfiguration.ResolveRoot(null, new Env(content, "Development"))));
    }
}
