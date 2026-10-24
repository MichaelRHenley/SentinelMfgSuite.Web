using System;

namespace SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

/// <summary>
/// Resolves a questionnaire schema version string to its corresponding
/// <see cref="QuestionnaireDefinition"/>. Resolution is strict: unsupported,
/// missing, or invalid versions are rejected rather than silently falling
/// back to a default or "latest" definition.
/// </summary>
public static class QuestionnaireDefinitionResolver
{
    /// <summary>The questionnaire version assigned to newly provisioned assessments.</summary>
    public const string CurrentVersion = "3.0";

    /// <summary>
    /// Resolves the <see cref="QuestionnaireDefinition"/> for the given questionnaire version.
    /// </summary>
    /// <param name="questionnaireVersion">The questionnaire schema version (e.g. "1.0").</param>
    /// <returns>The matching <see cref="QuestionnaireDefinition"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="questionnaireVersion"/> is null, empty, whitespace, or not a
    /// recognized questionnaire version.
    /// </exception>
    public static QuestionnaireDefinition Resolve(string? questionnaireVersion)
    {
        if (string.IsNullOrWhiteSpace(questionnaireVersion))
        {
            throw new ArgumentException("Questionnaire version is required.", nameof(questionnaireVersion));
        }

        return questionnaireVersion switch
        {
            "1.0" => QuestionnaireV1.Definition,
            "2.0" => QuestionnaireV2.Definition,
            "3.0" => QuestionnaireV3.Definition,
            _ => throw new ArgumentException(
                $"Unsupported questionnaire version '{questionnaireVersion}'.",
                nameof(questionnaireVersion))
        };
    }
}
