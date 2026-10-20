using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports;

public enum AssessmentReportTransitionFailure
{
    None = 0,
    InvalidTransition,
    Forbidden,
    NotFound,
    ConcurrencyConflict,
    ValidationFailed,
    InvalidReason
}

/// <summary>
/// Outcome of a pure transition evaluation. <see cref="RequiresReplacementIssuance"/> is true only for
/// Issued to Superseded, which may be performed solely as part of issuing a replacement revision.
/// </summary>
public sealed record AssessmentReportTransitionResult(
    bool Succeeded,
    AssessmentReportTransitionFailure Failure = AssessmentReportTransitionFailure.None,
    bool RequiresReplacementIssuance = false,
    string? NormalizedReason = null)
{
    public static AssessmentReportTransitionResult Allowed(bool requiresReplacementIssuance = false, string? normalizedReason = null)
        => new(true, AssessmentReportTransitionFailure.None, requiresReplacementIssuance, normalizedReason);

    public static AssessmentReportTransitionResult Failed(AssessmentReportTransitionFailure failure)
        => new(false, failure);
}

/// <summary>
/// Pure, deterministic lifecycle rules for <see cref="AssessmentReportRevisionStatus"/>.
/// Rejected and Superseded are terminal. Correcting a Rejected revision is a new Draft revision (not a transition).
/// Approved to Superseded is intentionally not supported.
/// </summary>
public static class AssessmentReportTransitionPolicy
{
    /// <summary>Maximum length of a rejection reason after trimming.</summary>
    public const int MaxReasonLength = 2000;

    private static readonly IReadOnlyDictionary<AssessmentReportRevisionStatus, AssessmentReportRevisionStatus[]> Allowed =
        new Dictionary<AssessmentReportRevisionStatus, AssessmentReportRevisionStatus[]>
        {
            [AssessmentReportRevisionStatus.Draft] = [AssessmentReportRevisionStatus.InReview],
            [AssessmentReportRevisionStatus.InReview] = [AssessmentReportRevisionStatus.Approved, AssessmentReportRevisionStatus.Rejected],
            [AssessmentReportRevisionStatus.Approved] = [AssessmentReportRevisionStatus.Rejected, AssessmentReportRevisionStatus.Issued],
            [AssessmentReportRevisionStatus.Issued] = [AssessmentReportRevisionStatus.Superseded],
            [AssessmentReportRevisionStatus.Rejected] = [],
            [AssessmentReportRevisionStatus.Superseded] = []
        };

    public static bool IsTerminal(AssessmentReportRevisionStatus status)
        => status is AssessmentReportRevisionStatus.Rejected or AssessmentReportRevisionStatus.Superseded;

    public static bool RequiresReason(AssessmentReportRevisionStatus target)
        => target == AssessmentReportRevisionStatus.Rejected;

    /// <summary>Evaluates a status pair only. Does not validate reasons.</summary>
    public static AssessmentReportTransitionResult Evaluate(
        AssessmentReportRevisionStatus from,
        AssessmentReportRevisionStatus to)
    {
        if (!Allowed.TryGetValue(from, out var targets) || !targets.Contains(to))
        {
            return AssessmentReportTransitionResult.Failed(AssessmentReportTransitionFailure.InvalidTransition);
        }

        return AssessmentReportTransitionResult.Allowed(
            requiresReplacementIssuance: to == AssessmentReportRevisionStatus.Superseded);
    }

    /// <summary>
    /// Evaluates a status pair and, when the target is Rejected, the reason (trimmed, non-empty, at most
    /// <see cref="MaxReasonLength"/>). For every other target the reason is ignored and NormalizedReason is null.
    /// </summary>
    public static AssessmentReportTransitionResult Evaluate(
        AssessmentReportRevisionStatus from,
        AssessmentReportRevisionStatus to,
        string? reason)
    {
        var result = Evaluate(from, to);
        if (!result.Succeeded || !RequiresReason(to))
        {
            return result;
        }

        return ValidateReason(reason) is { } normalized
            ? result with { NormalizedReason = normalized }
            : AssessmentReportTransitionResult.Failed(AssessmentReportTransitionFailure.InvalidReason);
    }

    /// <summary>
    /// Returns the trimmed reason when it is non-empty and at most <see cref="MaxReasonLength"/> characters; otherwise null.
    /// </summary>
    public static string? ValidateReason(string? reason)
    {
        var trimmed = reason?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxReasonLength ? null : trimmed;
    }
}
