using System;
using System.Threading;
using System.Threading.Tasks;

namespace SentinelMfgSuite.Web.Services.Assessments;

public interface IAssessmentInvitationAccessService
{
    Task<AssessmentInvitationAccessResult> ValidateAccessAsync(Guid invitationId, string? invitationToken, CancellationToken ct = default);
}
