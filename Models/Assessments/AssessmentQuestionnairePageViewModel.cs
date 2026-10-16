using System;
using System.Collections.Generic;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

namespace SentinelMfgSuite.Web.Models.Assessments;

public sealed class AssessmentQuestionnairePageViewModel
{
    public Guid InvitationId { get; init; }

    public string InvitationToken { get; init; } = string.Empty;

    public string StatusLabel { get; init; } = string.Empty;

    public bool IsReadOnly { get; init; }

    public bool CanSaveDraft { get; init; }

    public string SaveDraftUrl { get; init; } = string.Empty;

    public string? InitialConcurrencyStamp { get; init; }

    public int TotalSections { get; init; }

    public int TotalQuestions { get; init; }

    public QuestionnaireDefinition Definition { get; init; } = QuestionnaireV1.Definition;

    public QuestionnaireResponseV1 InitialResponse { get; init; } = new();

    public IReadOnlyDictionary<string, string> PriorityChoicesByValue { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
