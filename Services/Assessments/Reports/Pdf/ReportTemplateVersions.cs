namespace SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;

/// <summary>Stable identifiers for PDF report templates.</summary>
public static class ReportTemplateVersions
{
    public const string V1 = "sentinel-assessment-report/1";

    /// <summary>Adds scoring interpretation, readable evidence and business-case framing. V1 stays frozen.</summary>
    public const string V2 = "sentinel-assessment-report/2";

    public const string V3 = "sentinel-assessment-report/3";

    /// <summary>The template assigned to newly created Draft revisions.</summary>
    public const string Current = V3;
}

public interface IReportPdfTemplate
{
    string Version { get; }

    byte[] Render(ReportPdfRenderModel model);
}

public sealed record ReportTemplateResolution(
    IReportPdfTemplate? Template,
    string? ResolvedVersion,
    bool IsLegacyNullCompatibility)
{
    public bool IsSupported => Template is not null;
}

public interface IReportPdfTemplateRegistry
{
    /// <summary>
    /// Resolves a persisted template version. A null or blank value is legacy data and resolves to v1 without
    /// rewriting the record. Any other unknown value is unsupported and never falls back to another template.
    /// </summary>
    ReportTemplateResolution Resolve(string? persistedVersion);
}

public sealed class ReportPdfTemplateRegistry : IReportPdfTemplateRegistry
{
    private readonly IReadOnlyDictionary<string, IReportPdfTemplate> _templates;

    public ReportPdfTemplateRegistry()
        : this(new IReportPdfTemplate[] { new SentinelAssessmentReportTemplateV1(), new SentinelAssessmentReportTemplateV2(), new SentinelAssessmentReportTemplateV3() })
    {
    }

    public ReportPdfTemplateRegistry(IEnumerable<IReportPdfTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        _templates = templates.ToDictionary(template => template.Version, StringComparer.Ordinal);
    }

    public ReportTemplateResolution Resolve(string? persistedVersion)
    {
        var legacy = string.IsNullOrWhiteSpace(persistedVersion);
        var version = legacy ? ReportTemplateVersions.V1 : persistedVersion!;
        return _templates.TryGetValue(version, out var template)
            ? new ReportTemplateResolution(template, version, legacy)
            : new ReportTemplateResolution(null, null, false);
    }
}

public sealed class ReportPdfTemplateUnsupportedException : InvalidOperationException
{
    public ReportPdfTemplateUnsupportedException(string? version)
        : base($"Report template version '{version}' is not supported.")
    {
    }
}
