namespace SentinelMfgSuite.Web.Models.Assessments;

public sealed class AssessmentQuestionnaireWelcomeViewModel
{
    public bool IsInvitationAvailable { get; init; }

    public string StatusLabel { get; init; } = string.Empty;

    public string StatusDescription { get; init; } = string.Empty;

    public bool IsReadOnly { get; init; }

    public string PrimaryActionLabel { get; init; } = string.Empty;

    public bool PrimaryActionEnabled { get; init; }

    public string? PrimaryActionUrl { get; init; }

    public string PrimaryActionNote { get; init; } = string.Empty;

    public int SectionCount { get; init; } = 6;

    public int QuestionCount { get; init; } = 30;
}
