using System;
using System.Collections.Generic;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

namespace SentinelMfgSuite.Web.Services.Assessments;

public enum AssessmentQuestionnaireLoadState
{
    Success = 0,
    AccessDenied = 1,
    ValidationFailure = 2,
}

public enum AssessmentQuestionnaireSaveState
{
    Success = 0,
    AccessDenied = 1,
    ValidationFailure = 2,
    ConcurrencyConflict = 3,
    AlreadySubmitted = 4,
}

public sealed record AssessmentQuestionnaireLoadResult(
    AssessmentQuestionnaireLoadState State,
    QuestionnaireResponseV1? Response,
    string? ConcurrencyStamp,
    bool IsReadOnly,
    IReadOnlyList<QuestionnaireValidationError> ValidationErrors)
{
    public bool IsSuccess => State == AssessmentQuestionnaireLoadState.Success;

    public static AssessmentQuestionnaireLoadResult Success(QuestionnaireResponseV1 response, string? concurrencyStamp, bool isReadOnly)
        => new(AssessmentQuestionnaireLoadState.Success, response, concurrencyStamp, isReadOnly, Array.Empty<QuestionnaireValidationError>());

    public static AssessmentQuestionnaireLoadResult AccessDenied()
        => new(AssessmentQuestionnaireLoadState.AccessDenied, null, null, false, Array.Empty<QuestionnaireValidationError>());

    public static AssessmentQuestionnaireLoadResult ValidationFailure(IReadOnlyList<QuestionnaireValidationError> errors, bool isReadOnly = false)
        => new(AssessmentQuestionnaireLoadState.ValidationFailure, null, null, isReadOnly, errors);
}

public sealed record AssessmentQuestionnaireSaveResult(
    AssessmentQuestionnaireSaveState State,
    string? ConcurrencyStamp,
    IReadOnlyList<QuestionnaireValidationError> ValidationErrors)
{
    public bool IsSuccess => State == AssessmentQuestionnaireSaveState.Success;

    public static AssessmentQuestionnaireSaveResult Success(string concurrencyStamp)
        => new(AssessmentQuestionnaireSaveState.Success, concurrencyStamp, Array.Empty<QuestionnaireValidationError>());

    public static AssessmentQuestionnaireSaveResult AccessDenied()
        => new(AssessmentQuestionnaireSaveState.AccessDenied, null, Array.Empty<QuestionnaireValidationError>());

    public static AssessmentQuestionnaireSaveResult ValidationFailure(IReadOnlyList<QuestionnaireValidationError> errors)
        => new(AssessmentQuestionnaireSaveState.ValidationFailure, null, errors);

    public static AssessmentQuestionnaireSaveResult ConcurrencyConflict()
        => new(AssessmentQuestionnaireSaveState.ConcurrencyConflict, null, Array.Empty<QuestionnaireValidationError>());

    public static AssessmentQuestionnaireSaveResult AlreadySubmitted()
        => new(AssessmentQuestionnaireSaveState.AlreadySubmitted, null, Array.Empty<QuestionnaireValidationError>());
}

public enum AssessmentQuestionnaireSubmitState
{
    Success = 0,
    AccessDenied = 1,
    ValidationFailure = 2,
    ConcurrencyConflict = 3,
    AlreadySubmitted = 4,
}

public sealed record AssessmentQuestionnaireSubmitResult(
    AssessmentQuestionnaireSubmitState State,
    string? ConcurrencyStamp,
    IReadOnlyList<QuestionnaireValidationError> ValidationErrors)
{
    public bool IsSuccess => State == AssessmentQuestionnaireSubmitState.Success;

    public static AssessmentQuestionnaireSubmitResult Success(string concurrencyStamp)
        => new(AssessmentQuestionnaireSubmitState.Success, concurrencyStamp, Array.Empty<QuestionnaireValidationError>());

    public static AssessmentQuestionnaireSubmitResult AccessDenied()
        => new(AssessmentQuestionnaireSubmitState.AccessDenied, null, Array.Empty<QuestionnaireValidationError>());

    public static AssessmentQuestionnaireSubmitResult ValidationFailure(IReadOnlyList<QuestionnaireValidationError> errors)
        => new(AssessmentQuestionnaireSubmitState.ValidationFailure, null, errors);

    public static AssessmentQuestionnaireSubmitResult ConcurrencyConflict()
        => new(AssessmentQuestionnaireSubmitState.ConcurrencyConflict, null, Array.Empty<QuestionnaireValidationError>());

    public static AssessmentQuestionnaireSubmitResult AlreadySubmitted()
        => new(AssessmentQuestionnaireSubmitState.AlreadySubmitted, null, Array.Empty<QuestionnaireValidationError>());
}
