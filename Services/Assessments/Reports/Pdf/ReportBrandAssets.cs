namespace SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;

/// <summary>Optimized copies of the official Sentinel logos, embedded in the assembly for PDF rendering.</summary>
internal static class ReportBrandAssets
{
    private static readonly Lazy<byte[]> White = new(() => Load("sentinel-logo-white.png"));
    private static readonly Lazy<byte[]> Dark = new(() => Load("sentinel-logo-dark.png"));
    private static readonly Lazy<byte[]> Emblem = new(() => Load("sentinel-emblem-dark.png"));

    /// <summary>Full logo (emblem and wordmark) in white, for the dark cover.</summary>
    public static byte[] LogoWhite => White.Value;

    /// <summary>Full logo in its dark colourway, for light backgrounds.</summary>
    public static byte[] LogoDark => Dark.Value;

    /// <summary>Emblem only, dark colourway, for interior page headers.</summary>
    public static byte[] EmblemDark => Emblem.Value;

    private static byte[] Load(string name)
    {
        var assembly = typeof(ReportBrandAssets).Assembly;
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith("." + name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded report image '{name}' is missing.");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
