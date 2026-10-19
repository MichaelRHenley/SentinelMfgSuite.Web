using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

namespace SentinelMfgSuite.Web.Services.Assessments.Scoring;

public interface IReadinessScoringService
{
    ReadinessScoringResult Score(string? questionnaireVersion, QuestionnaireResponseV1? response);
}
