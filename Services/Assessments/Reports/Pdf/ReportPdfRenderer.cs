using System.Reflection;
using QuestPDF.Drawing;
using QuestPDF.Infrastructure;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;

/// <summary>
/// One-time QuestPDF configuration: Community license declaration, embedded Lato fonts only
/// (no environment fonts, so output does not depend on the host).
/// </summary>
internal static class ReportPdfRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    private static readonly string[] FontResources =
    {
        "Lato-Regular.ttf",
        "Lato-Bold.ttf",
        "Lato-Italic.ttf"
    };

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.UseSystemFonts = false;

            var assembly = typeof(ReportPdfRuntime).Assembly;
            var names = assembly.GetManifestResourceNames();
            foreach (var font in FontResources)
            {
                var resource = names.SingleOrDefault(name => name.EndsWith("." + font, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"Embedded report font '{font}' is missing.");
                using var stream = assembly.GetManifestResourceStream(resource)!;
                FontManager.RegisterFontFromStream(stream);
            }

            _initialized = true;
        }
    }
}

public sealed class ReportPdfRenderer : IReportPdfRenderer
{
    private readonly IReportPdfTemplateRegistry _registry;

    public ReportPdfRenderer(IReportPdfTemplateRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public byte[] Render(ReportPdfRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var resolution = _registry.Resolve(model.TemplateVersion);
        if (resolution.Template is null)
        {
            throw new ReportPdfTemplateUnsupportedException(model.TemplateVersion);
        }

        return resolution.Template.Render(model);
    }
}
