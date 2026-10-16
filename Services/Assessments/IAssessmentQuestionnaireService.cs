using System;
using System.Threading;
using System.Threading.Tasks;

namespace SentinelMfgSuite.Web.Services.Assessments;

public interface IAssessmentQuestionnaireService
{
    Task<AssessmentQuestionnaireLoadResult> LoadDraftAsync(Guid invitationId, string? invitationToken, CancellationToken ct = default);

    Task<AssessmentQuestionnaireSaveResult> SaveDraftAsync(Guid invitationId, string? invitationToken, string? questionnairePayload, string? concurrencyStamp, CancellationToken ct = default);

    Task<AssessmentQuestionnaireSubmitResult> SubmitAsync(Guid invitationId, string? invitationToken, string? questionnairePayload, string? concurrencyStamp, CancellationToken ct = default);
}
