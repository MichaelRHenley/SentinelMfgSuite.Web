using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports;

/// <summary>
/// Builds the read-only inputs for report content validation from persisted source data.
/// It never recalculates scoring and never mutates the revision or consultant content.
/// </summary>
internal static class AssessmentReportValidationContextBuilder
{
    /// <summary>
    /// Returns null when the source data is missing or inconsistent.
    /// The revision must be loaded with its <see cref="AssessmentReport"/>.
    /// </summary>
    public static async Task<AssessmentReportContentValidationContext?> BuildAsync(
        ApplicationDbContext db,
        AssessmentReportRevision revision,
        AssessmentReportContentStage stage,
        CancellationToken cancellationToken)
    {
        var report = revision.AssessmentReport;
        if (report is null)
        {
            return null;
        }

        var source = await db.Set<AssessmentResponse>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == report.SubmittedResponseId, cancellationToken);
        if (source is null || source.AssessmentId != report.AssessmentId || source.IsDraft || source.SubmittedUtc is null)
        {
            return null;
        }

        try
        {
            var definition = QuestionnaireDefinitionResolver.Resolve(report.QuestionnaireVersion);
            var response = QuestionnaireResponseV1Serializer.Deserialize(source.Data);
            if (response is null)
            {
                return null;
            }
            var sourceValidation = QuestionnaireResponseV1Validator.Validate(response, definition, QuestionnaireValidationMode.Submission);
            if (!sourceValidation.IsValid)
            {
                return null;
            }
            if (revision.ScoringSnapshot is null || revision.ScoringSnapshotSchemaVersion != ReadinessScoringSnapshotSerializer.PersistedSchemaVersion)
            {
                return null;
            }
            var envelope = ReadinessScoringSnapshotSerializer.Deserialize(revision.ScoringSnapshot);
            var snapshot = envelope.Snapshot;
            if (!string.Equals(snapshot.QuestionnaireVersion, report.QuestionnaireVersion, StringComparison.Ordinal))
            {
                return null;
            }

            return new AssessmentReportContentValidationContext(stage, definition, response, snapshot);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
