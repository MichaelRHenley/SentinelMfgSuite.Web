using SentinelMfgSuite.Web.Models.Assessments;
using System.Security.Claims;

namespace SentinelMfgSuite.Web.Services.Assessments;

public interface IAssessmentReportService
{
    Task<AssessmentReportServiceResult> CreateAsync(
        Guid assessmentId,
        Guid submittedResponseId,
        ClaimsPrincipal caller,
        CancellationToken ct = default);

    Task<AssessmentReportServiceResult> GetByIdAsync(
        Guid reportId,
        ClaimsPrincipal caller,
        CancellationToken ct = default);

    Task<AssessmentReportServiceResult> GetBySubmittedResponseIdAsync(
        Guid submittedResponseId,
        ClaimsPrincipal caller,
        CancellationToken ct = default);
}
