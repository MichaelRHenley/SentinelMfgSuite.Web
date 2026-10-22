using System.Text.Json;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;

/// <summary>
/// Builds the render model from frozen revision data only. It never calls live scoring, never queries
/// assessment responses and never resolves questionnaire definitions.
/// </summary>
public sealed class ReportPdfModelBuilder : IReportPdfModelBuilder
{
    private const double ScoreTolerance = 1e-9;

    private readonly IReportPdfTemplateRegistry _registry;

    public ReportPdfModelBuilder(IReportPdfTemplateRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public ReportPdfModelBuildResult Build(ReportPdfSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var report = source.Report;
        var revision = source.Revision;

        if (revision.AssessmentReportId != report.Id)
        {
            return Fail("revision-report-mismatch", "The revision does not belong to the supplied report.");
        }

        if (revision.Status is not (AssessmentReportRevisionStatus.Issued or AssessmentReportRevisionStatus.Superseded)
            || revision.IssuedUtc is null)
        {
            return Fail("revision-not-issued", "Only an issued revision can be rendered.");
        }

        var template = _registry.Resolve(revision.ReportTemplateVersion);
        if (!template.IsSupported)
        {
            return Fail("template-unsupported", $"Report template version '{revision.ReportTemplateVersion}' is not supported.");
        }

        if (string.IsNullOrWhiteSpace(revision.ConsultantContentJson))
        {
            return Fail("content-missing", "The revision has no frozen consultant content.");
        }

        ConsultantReportContent content;
        try
        {
            content = ConsultantReportContentSerializer.Deserialize(revision.ConsultantContentJson);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return Fail("content-unsupported", "The frozen consultant content is invalid or uses an unsupported schema version.");
        }

        if (string.IsNullOrWhiteSpace(revision.ScoringSnapshot))
        {
            return Fail("scoring-snapshot-missing", "The revision has no frozen scoring snapshot.");
        }

        if (revision.ScoringSnapshotSchemaVersion != ReadinessScoringSnapshotSerializer.PersistedSchemaVersion)
        {
            return Fail("scoring-snapshot-unsupported", "The frozen scoring snapshot uses an unsupported schema version.");
        }

        ReadinessScoringSnapshotEnvelope envelope;
        try
        {
            envelope = ReadinessScoringSnapshotSerializer.Deserialize(revision.ScoringSnapshot);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return Fail("scoring-snapshot-unsupported", "The frozen scoring snapshot is invalid or uses an unsupported schema version.");
        }

        var snapshot = envelope.Snapshot;
        if (snapshot is null)
        {
            return Fail("scoring-snapshot-missing", "The frozen scoring snapshot is empty.");
        }

        if (!string.Equals(snapshot.QuestionnaireVersion, report.QuestionnaireVersion, StringComparison.Ordinal))
        {
            return Fail("questionnaire-version-mismatch", "The scoring snapshot questionnaire version does not match the report.");
        }

        var mismatches = CrossCheckReadiness(content.ExecutiveSummary.Readiness, snapshot);
        if (mismatches.Count > 0)
        {
            return ReportPdfModelBuildResult.Failure(mismatches);
        }

        return ReportPdfModelBuildResult.Success(new ReportPdfRenderModel(
            template.ResolvedVersion!,
            template.IsLegacyNullCompatibility,
            report.Id,
            revision.Id,
            revision.RevisionNumber,
            revision.Status,
            source.AssessmentTitle,
            source.SiteName,
            DateTime.SpecifyKind(revision.IssuedUtc.Value, DateTimeKind.Utc),
            revision.IssuedByUserId,
            revision.ApprovedUtc is { } approved ? DateTime.SpecifyKind(approved, DateTimeKind.Utc) : null,
            revision.ApprovedByUserId,
            revision.BasedOnRevisionId,
            revision.CorrectionReason,
            report.QuestionnaireVersion,
            DateTime.SpecifyKind(report.ResponseSubmittedUtc, DateTimeKind.Utc),
            revision.ScoringContractVersion,
            envelope.SchemaVersion,
            content.SchemaVersion,
            content,
            snapshot));
    }

    private static ReportPdfModelBuildResult Fail(string code, string message)
        => ReportPdfModelBuildResult.Failure(new ReportPdfValidationError(code, message));

    private static List<ReportPdfValidationError> CrossCheckReadiness(
        ReadinessScoreRepresentation? copy,
        ReadinessScoringSnapshot snapshot)
    {
        var errors = new List<ReportPdfValidationError>();
        if (copy is null)
        {
            return errors;
        }

        void Mismatch(string what) => errors.Add(new ReportPdfValidationError(
            "readiness-mismatch", $"Readiness in consultant content disagrees with the frozen scoring snapshot: {what}."));

        if (copy.OverallStatus != snapshot.OverallStatus) Mismatch("overall status");
        if (!SameScore(copy.OverallRawScore0To100, snapshot.OverallRawScore0To100)) Mismatch("overall raw score");
        if (copy.OverallDisplayScore0To100 != snapshot.OverallDisplayScore0To100) Mismatch("overall display score");
        if (copy.OverallClassification != snapshot.OverallClassification) Mismatch("overall classification");

        var copyDimensions = copy.Dimensions ?? Array.Empty<ReadinessDimensionRepresentation>();
        if (copyDimensions.Count != snapshot.Dimensions.Count)
        {
            Mismatch("dimension count");
        }

        foreach (var dimension in copyDimensions)
        {
            var frozen = snapshot.Dimensions.FirstOrDefault(item => item.DimensionId == dimension.DimensionId);
            if (frozen is null)
            {
                Mismatch($"dimension '{dimension.DimensionId}' is not in the snapshot");
                continue;
            }

            if (dimension.Status != frozen.Status
                || !SameScore(dimension.RawScore0To100, frozen.RawScore0To100)
                || dimension.DisplayScore0To100 != frozen.DisplayScore0To100
                || dimension.Classification != frozen.Classification)
            {
                Mismatch($"dimension '{dimension.DimensionId}'");
            }
        }

        return errors;
    }

    private static bool SameScore(double? left, double? right)
        => left is null || right is null ? left is null && right is null : Math.Abs(left.Value - right.Value) <= ScoreTolerance;
}
