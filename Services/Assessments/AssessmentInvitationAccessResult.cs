using System;

namespace SentinelMfgSuite.Web.Services.Assessments;

public enum AssessmentInvitationAccessState
{
    Denied = 0,
    Expired = 1,
    Revoked = 2,
    Ineligible = 3,
    Authorized = 4,
}

public enum AssessmentInvitationResponseState
{
    NotStarted = 0,
    Draft = 1,
    Submitted = 2,
}

public sealed record AssessmentInvitationAccessResult(
    AssessmentInvitationAccessState AccessState,
    Guid? AssessmentId,
    Guid? InvitationId,
    Guid? ResponseId,
    AssessmentInvitationResponseState ResponseState,
    bool IsReadOnly)
{
    public bool IsAuthorized => AccessState == AssessmentInvitationAccessState.Authorized;

    public static AssessmentInvitationAccessResult Denied()
        => new(AssessmentInvitationAccessState.Denied, null, null, null, AssessmentInvitationResponseState.NotStarted, false);

    public static AssessmentInvitationAccessResult Expired()
        => new(AssessmentInvitationAccessState.Expired, null, null, null, AssessmentInvitationResponseState.NotStarted, false);

    public static AssessmentInvitationAccessResult Revoked()
        => new(AssessmentInvitationAccessState.Revoked, null, null, null, AssessmentInvitationResponseState.NotStarted, false);

    public static AssessmentInvitationAccessResult Ineligible()
        => new(AssessmentInvitationAccessState.Ineligible, null, null, null, AssessmentInvitationResponseState.NotStarted, false);

    public static AssessmentInvitationAccessResult Authorized(
        Guid assessmentId,
        Guid invitationId,
        Guid? responseId,
        AssessmentInvitationResponseState responseState,
        bool isReadOnly)
        => new(AssessmentInvitationAccessState.Authorized, assessmentId, invitationId, responseId, responseState, isReadOnly);
}
