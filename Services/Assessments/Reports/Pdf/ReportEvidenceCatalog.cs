using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;

/// <summary>
/// Customer-facing wording for frozen scoring evidence. Question and answer text is looked up in the
/// immutable questionnaire definition for the questionnaire version recorded in the frozen scoring
/// snapshot, never in the current questionnaire and never in submitted responses. When that version does
/// not define the item or the answer code, no wording is returned and the caller shows the raw frozen
/// identifiers instead of guessing.
/// </summary>
internal static class ReportEvidenceCatalog
{
    internal sealed record EvidenceWording(string Question, string? AnswerLabel);

    internal sealed record DimensionDescription(string WhatItMeasures, string DoesNotMeasure);

    private static readonly IReadOnlyDictionary<string, DimensionDescription> Dimensions =
        new Dictionary<string, DimensionDescription>(StringComparer.Ordinal)
        {
            ["D1"] = new(
                "how integrated systems are and how much of the data needed for operational work is available through automatic connections, and how reliably that data arrives.",
                "the quality of the data itself, or how it is reported and used."),
            ["D2"] = new(
                "whether historical operational data is available, whether timestamps and identifiers are consistent, whether equipment data can be linked to production context, and how data accuracy and completeness are checked.",
                "whether data is connected automatically, or how it is reported and used."),
            ["D3"] = new(
                "how operational KPIs are calculated and validated, how consistently reports and KPIs are reported to reflect production conditions, and how data and analytics are used in decisions.",
                "whether the underlying data is automatically connected or complete, or whether analytics cover all equipment, lines or sites. A high score reflects the answers given to these questions only.")
        };

    /// <summary>Dimension descriptions are defined by the scoring contract for questionnaire versions 2.0 and 3.0.</summary>
    internal static DimensionDescription? FindDimension(string? questionnaireVersion, string? dimensionId)
    {
        if (questionnaireVersion is not ("2.0" or "3.0") || dimensionId is null)
        {
            return null;
        }

        return Dimensions.TryGetValue(dimensionId, out var description) ? description : null;
    }

    /// <summary>
    /// Returns the question and answer wording defined for the item in the given questionnaire version,
    /// or null when the version, question or field cannot be established.
    /// </summary>
    internal static EvidenceWording? Find(string? questionnaireVersion, string? questionId, string? fieldId, string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(questionnaireVersion) || string.IsNullOrWhiteSpace(questionId))
        {
            return null;
        }

        QuestionnaireDefinition definition;
        try
        {
            definition = QuestionnaireDefinitionResolver.Resolve(questionnaireVersion);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var question = definition.Sections
            .SelectMany(section => section.Questions)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, questionId, StringComparison.Ordinal));
        if (question is null)
        {
            return null;
        }

        IEnumerable<Choice>? choices;
        if (string.IsNullOrEmpty(fieldId))
        {
            choices = question.Choices;
        }
        else
        {
            var field = question.RepeatableFields?.FirstOrDefault(candidate => string.Equals(candidate.Id, fieldId, StringComparison.Ordinal));
            if (field is null)
            {
                return null;
            }

            choices = field.Choices;
        }

        string? label = null;
        if (!string.IsNullOrEmpty(rawValue))
        {
            label = choices?.FirstOrDefault(choice => string.Equals(choice.Value, rawValue, StringComparison.Ordinal))?.Label;
        }

        return new EvidenceWording(question.Text, label);
    }
}
