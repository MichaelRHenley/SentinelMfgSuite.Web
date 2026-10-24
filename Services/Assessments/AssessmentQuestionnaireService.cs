using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

namespace SentinelMfgSuite.Web.Services.Assessments;

public sealed class AssessmentQuestionnaireService : IAssessmentQuestionnaireService
{
    private readonly ApplicationDbContext _db;
    private readonly IAssessmentInvitationAccessService _accessService;

    private readonly SentinelMfgSuite.Web.Services.Email.IEmailOutboxWriter? _outbox;

    public AssessmentQuestionnaireService(ApplicationDbContext db, IAssessmentInvitationAccessService accessService)
        : this(db, accessService, null)
    {
    }

    public AssessmentQuestionnaireService(ApplicationDbContext db, IAssessmentInvitationAccessService accessService, SentinelMfgSuite.Web.Services.Email.IEmailOutboxWriter? outbox)
    {
        _db = db;
        _accessService = accessService;
        _outbox = outbox;
    }

    public async Task<AssessmentQuestionnaireLoadResult> LoadDraftAsync(Guid invitationId, string? invitationToken, CancellationToken ct = default)
    {
        var access = await _accessService.ValidateAccessAsync(invitationId, invitationToken, ct);
        if (!access.IsAuthorized || access.AssessmentId is null || access.InvitationId is null)
        {
            return AssessmentQuestionnaireLoadResult.AccessDenied();
        }

        var assessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == access.AssessmentId.Value, ct);

        if (assessment is null)
        {
            return AssessmentQuestionnaireLoadResult.AccessDenied();
        }

        QuestionnaireDefinition definition;
        try
        {
            definition = QuestionnaireDefinitionResolver.Resolve(assessment.QuestionnaireVersion);
        }
        catch (ArgumentException)
        {
            return AssessmentQuestionnaireLoadResult.UnsupportedQuestionnaireVersion();
        }

        var response = await _db.Set<AssessmentResponse>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.InvitationId == access.InvitationId.Value, ct);

        if (response is null)
        {
            return AssessmentQuestionnaireLoadResult.Success(CreateEmptyResponse(definition), null, access.IsReadOnly, definition);
        }

        if (response.AssessmentId != access.AssessmentId.Value)
        {
            return AssessmentQuestionnaireLoadResult.AccessDenied();
        }

        var deserialized = DeserializePayload(response.Data, out var errors);
        if (errors.Count > 0)
        {
            return AssessmentQuestionnaireLoadResult.ValidationFailure(errors, access.IsReadOnly);
        }

        if (!string.Equals(deserialized!.SchemaVersion, definition.SchemaVersion, StringComparison.Ordinal))
        {
            return AssessmentQuestionnaireLoadResult.ValidationFailure(
                new[]
                {
                    new QuestionnaireValidationError(string.Empty, null, $"Schema version must be '{definition.SchemaVersion}'.")
                },
                access.IsReadOnly);
        }

        return AssessmentQuestionnaireLoadResult.Success(deserialized, response.ConcurrencyStamp, access.IsReadOnly, definition);
    }

    public async Task<AssessmentQuestionnaireSaveResult> SaveDraftAsync(Guid invitationId, string? invitationToken, string? questionnairePayload, string? concurrencyStamp, CancellationToken ct = default)
    {
        var access = await _accessService.ValidateAccessAsync(invitationId, invitationToken, ct);
        if (!access.IsAuthorized || access.AssessmentId is null || access.InvitationId is null)
        {
            return AssessmentQuestionnaireSaveResult.AccessDenied();
        }

        if (access.IsReadOnly || access.ResponseState == AssessmentInvitationResponseState.Submitted)
        {
            return AssessmentQuestionnaireSaveResult.AlreadySubmitted();
        }

        var (assessmentExists, definition) = await ResolveAssessmentDefinitionAsync(access.AssessmentId.Value, ct);
        if (!assessmentExists)
        {
            return AssessmentQuestionnaireSaveResult.AccessDenied();
        }

        if (definition is null)
        {
            return AssessmentQuestionnaireSaveResult.UnsupportedQuestionnaireVersion();
        }

        if (string.IsNullOrWhiteSpace(questionnairePayload))
        {
            return AssessmentQuestionnaireSaveResult.ValidationFailure(new[]
            {
                new QuestionnaireValidationError(string.Empty, null, "Questionnaire payload is required.")
            });
        }

        var deserialized = DeserializePayload(questionnairePayload, out var deserializationErrors);
        if (deserializationErrors.Count > 0)
        {
            return AssessmentQuestionnaireSaveResult.ValidationFailure(deserializationErrors);
        }

        if (deserialized is null)
        {
            return AssessmentQuestionnaireSaveResult.ValidationFailure(new[]
            {
                new QuestionnaireValidationError(string.Empty, null, "Malformed questionnaire JSON.")
            });
        }

        var validation = QuestionnaireResponseV1Validator.Validate(deserialized, definition, QuestionnaireValidationMode.Draft);
        if (!validation.IsValid)
        {
            return AssessmentQuestionnaireSaveResult.ValidationFailure(validation.Errors);
        }

        var serializedPayload = QuestionnaireResponseV1Serializer.Serialize(deserialized);
        var nowUtc = DateTime.UtcNow;
        var newConcurrencyStamp = Guid.NewGuid().ToString();

        if (access.ResponseId is null)
        {
            var created = new AssessmentResponse
            {
                Id = Guid.NewGuid(),
                AssessmentId = access.AssessmentId.Value,
                InvitationId = access.InvitationId.Value,
                Data = serializedPayload,
                IsDraft = true,
                LastSavedUtc = nowUtc,
                SubmittedUtc = null,
                ConcurrencyStamp = newConcurrencyStamp
            };

            _db.Add(created);

            try
            {
                await _db.SaveChangesAsync(ct);
                return AssessmentQuestionnaireSaveResult.Success(created.ConcurrencyStamp);
            }
            catch (DbUpdateException)
            {
                _db.Entry(created).State = EntityState.Detached;
                return await TranslateDraftWriteConflictAsync(access.InvitationId.Value, ct);
            }
        }

        if (string.IsNullOrWhiteSpace(concurrencyStamp))
        {
            return AssessmentQuestionnaireSaveResult.ConcurrencyConflict();
        }

        var rowsUpdated = await _db.Set<AssessmentResponse>()
            .Where(x => x.Id == access.ResponseId.Value
                && x.InvitationId == access.InvitationId.Value
                && x.AssessmentId == access.AssessmentId.Value
                && x.ConcurrencyStamp == concurrencyStamp
                && x.IsDraft
                && x.SubmittedUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Data, serializedPayload)
                .SetProperty(x => x.IsDraft, true)
                .SetProperty(x => x.SubmittedUtc, (DateTime?)null)
                .SetProperty(x => x.LastSavedUtc, nowUtc)
                .SetProperty(x => x.ConcurrencyStamp, newConcurrencyStamp), ct);

        if (rowsUpdated == 1)
        {
            return AssessmentQuestionnaireSaveResult.Success(newConcurrencyStamp);
        }

        return await TranslateDraftUpdateMissAsync(access.InvitationId.Value, ct);
    }

    public async Task<AssessmentQuestionnaireSubmitResult> SubmitAsync(Guid invitationId, string? invitationToken, string? questionnairePayload, string? concurrencyStamp, CancellationToken ct = default)
    {
        var access = await _accessService.ValidateAccessAsync(invitationId, invitationToken, ct);
        if (!access.IsAuthorized || access.AssessmentId is null || access.InvitationId is null)
        {
            return AssessmentQuestionnaireSubmitResult.AccessDenied();
        }

        if (access.IsReadOnly || access.ResponseState == AssessmentInvitationResponseState.Submitted)
        {
            return AssessmentQuestionnaireSubmitResult.AlreadySubmitted();
        }

        var (assessmentExists, definition) = await ResolveAssessmentDefinitionAsync(access.AssessmentId.Value, ct);
        if (!assessmentExists)
        {
            return AssessmentQuestionnaireSubmitResult.AccessDenied();
        }

        if (definition is null)
        {
            return AssessmentQuestionnaireSubmitResult.UnsupportedQuestionnaireVersion();
        }

        if (string.IsNullOrWhiteSpace(questionnairePayload))
        {
            return AssessmentQuestionnaireSubmitResult.ValidationFailure(new[]
            {
                new QuestionnaireValidationError(string.Empty, null, "Questionnaire payload is required.")
            });
        }

        var deserialized = DeserializePayload(questionnairePayload, out var deserializationErrors);
        if (deserializationErrors.Count > 0)
        {
            return AssessmentQuestionnaireSubmitResult.ValidationFailure(deserializationErrors);
        }

        if (deserialized is null)
        {
            return AssessmentQuestionnaireSubmitResult.ValidationFailure(new[]
            {
                new QuestionnaireValidationError(string.Empty, null, "Malformed questionnaire JSON.")
            });
        }

        var validation = QuestionnaireResponseV1Validator.Validate(deserialized, definition, QuestionnaireValidationMode.Submission);
        if (!validation.IsValid)
        {
            return AssessmentQuestionnaireSubmitResult.ValidationFailure(validation.Errors);
        }

        var serializedPayload = QuestionnaireResponseV1Serializer.Serialize(deserialized);
        var nowUtc = DateTime.UtcNow;
        var newConcurrencyStamp = Guid.NewGuid().ToString();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            if (access.ResponseId is null)
            {
                var created = new AssessmentResponse
                {
                    Id = Guid.NewGuid(),
                    AssessmentId = access.AssessmentId.Value,
                    InvitationId = access.InvitationId.Value,
                    Data = serializedPayload,
                    IsDraft = false,
                    LastSavedUtc = nowUtc,
                    SubmittedUtc = nowUtc,
                    ConcurrencyStamp = newConcurrencyStamp
                };

                _db.Add(created);

                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    _db.Entry(created).State = EntityState.Detached;
                    await tx.RollbackAsync(ct);
                    return await TranslateSubmitInsertConflictAsync(access.InvitationId.Value, ct);
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(concurrencyStamp))
                {
                    await tx.RollbackAsync(ct);
                    return AssessmentQuestionnaireSubmitResult.ConcurrencyConflict();
                }

                var rowsUpdated = await _db.Set<AssessmentResponse>()
                    .Where(x => x.Id == access.ResponseId.Value
                        && x.InvitationId == access.InvitationId.Value
                        && x.AssessmentId == access.AssessmentId.Value
                        && x.ConcurrencyStamp == concurrencyStamp
                        && x.IsDraft
                        && x.SubmittedUtc == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Data, serializedPayload)
                        .SetProperty(x => x.IsDraft, false)
                        .SetProperty(x => x.SubmittedUtc, nowUtc)
                        .SetProperty(x => x.LastSavedUtc, nowUtc)
                        .SetProperty(x => x.ConcurrencyStamp, newConcurrencyStamp), ct);

                if (rowsUpdated != 1)
                {
                    await tx.RollbackAsync(ct);
                    return await TranslateSubmitUpdateMissAsync(access.InvitationId.Value, ct);
                }
            }

            var assessmentRowsUpdated = await _db.Set<Assessment>()
                .Where(x => x.Id == access.AssessmentId.Value
                    && (x.Status == AssessmentStatus.Invited || x.Status == AssessmentStatus.InProgress))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, AssessmentStatus.Submitted)
                    .SetProperty(x => x.UpdatedUtc, nowUtc), ct);

            if (assessmentRowsUpdated != 1)
            {
                await tx.RollbackAsync(ct);
                return await TranslateAssessmentSubmissionFailureAsync(access.AssessmentId.Value, access.InvitationId.Value, ct);
            }

            if (_outbox is not null)
            {
                await _outbox.EnqueueSubmissionNotificationsAsync(access.AssessmentId.Value, access.InvitationId.Value, nowUtc, ct);
            }

            await tx.CommitAsync(ct);
            return AssessmentQuestionnaireSubmitResult.Success(newConcurrencyStamp);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<(bool AssessmentExists, QuestionnaireDefinition? Definition)> ResolveAssessmentDefinitionAsync(Guid assessmentId, CancellationToken ct)
    {
        var assessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == assessmentId, ct);

        if (assessment is null)
        {
            return (false, null);
        }

        try
        {
            return (true, QuestionnaireDefinitionResolver.Resolve(assessment.QuestionnaireVersion));
        }
        catch (ArgumentException)
        {
            return (true, null);
        }
    }

    private static QuestionnaireResponseV1 CreateEmptyResponse(QuestionnaireDefinition definition)
        => new()
        {
            SchemaVersion = definition.SchemaVersion,
            Answers = new Dictionary<string, QuestionAnswer>()
        };

    private static QuestionnaireResponseV1? DeserializePayload(string json, out List<QuestionnaireValidationError> errors)
    {
        errors = new List<QuestionnaireValidationError>();

        try
        {
            var response = QuestionnaireResponseV1Serializer.Deserialize(json);
            if (response is null)
            {
                errors.Add(new QuestionnaireValidationError(string.Empty, null, "Questionnaire payload is required."));
                return null;
            }

            return response;
        }
        catch (JsonException)
        {
            errors.Add(new QuestionnaireValidationError(string.Empty, null, "Malformed questionnaire JSON."));
            return null;
        }
        catch (NotSupportedException)
        {
            errors.Add(new QuestionnaireValidationError(string.Empty, null, "Malformed questionnaire JSON."));
            return null;
        }
    }

    private async Task<AssessmentQuestionnaireSaveResult> TranslateDraftWriteConflictAsync(Guid invitationId, CancellationToken ct)
    {
        var current = await _db.Set<AssessmentResponse>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.InvitationId == invitationId, ct);

        if (current is not null && (current.SubmittedUtc.HasValue || !current.IsDraft))
        {
            return AssessmentQuestionnaireSaveResult.AlreadySubmitted();
        }

        return AssessmentQuestionnaireSaveResult.ConcurrencyConflict();
    }

    private async Task<AssessmentQuestionnaireSaveResult> TranslateDraftUpdateMissAsync(Guid invitationId, CancellationToken ct)
    {
        var current = await _db.Set<AssessmentResponse>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.InvitationId == invitationId, ct);

        if (current is not null && (current.SubmittedUtc.HasValue || !current.IsDraft))
        {
            return AssessmentQuestionnaireSaveResult.AlreadySubmitted();
        }

        return AssessmentQuestionnaireSaveResult.ConcurrencyConflict();
    }

    private async Task<AssessmentQuestionnaireSubmitResult> TranslateSubmitInsertConflictAsync(Guid invitationId, CancellationToken ct)
    {
        var current = await _db.Set<AssessmentResponse>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.InvitationId == invitationId, ct);

        if (current is not null && (current.SubmittedUtc.HasValue || !current.IsDraft))
        {
            return AssessmentQuestionnaireSubmitResult.AlreadySubmitted();
        }

        return AssessmentQuestionnaireSubmitResult.ConcurrencyConflict();
    }

    private async Task<AssessmentQuestionnaireSubmitResult> TranslateSubmitUpdateMissAsync(Guid invitationId, CancellationToken ct)
    {
        var current = await _db.Set<AssessmentResponse>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.InvitationId == invitationId, ct);

        if (current is not null && (current.SubmittedUtc.HasValue || !current.IsDraft))
        {
            return AssessmentQuestionnaireSubmitResult.AlreadySubmitted();
        }

        return AssessmentQuestionnaireSubmitResult.ConcurrencyConflict();
    }

    private async Task<AssessmentQuestionnaireSubmitResult> TranslateAssessmentSubmissionFailureAsync(Guid assessmentId, Guid invitationId, CancellationToken ct)
    {
        var currentAssessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == assessmentId, ct);

        if (currentAssessment is not null && currentAssessment.Status == AssessmentStatus.Submitted)
        {
            var currentResponse = await _db.Set<AssessmentResponse>()
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.InvitationId == invitationId, ct);

            if (currentResponse is not null && (currentResponse.SubmittedUtc.HasValue || !currentResponse.IsDraft))
            {
                return AssessmentQuestionnaireSubmitResult.AlreadySubmitted();
            }

            return AssessmentQuestionnaireSubmitResult.ConcurrencyConflict();
        }

        return AssessmentQuestionnaireSubmitResult.AccessDenied();
    }
}
