namespace SentinelMfgSuite.Web.Tests;

internal static class ValidationPdfOutput
{
    private const string Variable = "SENTINEL_VALIDATION_PDF_DIR";

    /// <summary>
    /// Opt-in review output. Writes nothing unless the variable is set, and always uses a
    /// per-owner file name written to a unique temp file then moved, so parallel tests never collide.
    /// </summary>
    public static void Save(string owner, string name, byte[] pdf)
    {
        var dir = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"{name}.{owner}.pdf");
        var temp = Path.Combine(dir, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, pdf);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
