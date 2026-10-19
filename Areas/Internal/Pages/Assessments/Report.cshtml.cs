using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;

namespace SentinelMfgSuite.Web.Areas.Internal.Pages.Assessments;

[Authorize(Policy = "SentinelAssessmentAdmin")]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ReportModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly IAssessmentReportService _reportService;
    private readonly IAssessmentReportRevisionContentService _contentService;

    public ReportModel(
        ApplicationDbContext db,
        IAssessmentReportService reportService,
        IAssessmentReportRevisionContentService contentService)
    {
        _db = db;
        _reportService = reportService;
        _contentService = contentService;
    }

    [BindProperty(SupportsGet = true)]
    public Guid AssessmentId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid SubmittedResponseId { get; set; }

    [BindProperty]
    public ExecutiveSummaryInput Input { get; set; } = new();

    [BindProperty]
    public IndustrialDataInput IndustrialInput { get; set; } = new();

    [BindProperty]
    public Guid ExpectedConcurrencyStamp { get; set; }

    public string AssessmentTitle { get; private set; } = string.Empty;
    public string SiteName { get; private set; } = string.Empty;
    public DateTime SubmittedUtc { get; private set; }
    public bool CanCreateReport { get; private set; }
    public int RevisionNumber { get; private set; }
    public AssessmentReportRevisionStatus? RevisionStatus { get; private set; }
    [BindProperty]
    public Guid? RevisionId { get; set; }
    public ConsultantReportContent? ReportContent { get; private set; }
    public IReadOnlyList<QuestionnaireSectionView> QuestionnaireSections { get; private set; } = Array.Empty<QuestionnaireSectionView>();
    public ReadinessScoreView? Scoring { get; private set; }
    public DateTime? LastSavedUtc { get; private set; }
    public IReadOnlyList<QuestionnaireAnswerView> EvidenceOptions { get; private set; } = Array.Empty<QuestionnaireAnswerView>();
    public IReadOnlyList<ReportContentValidationIssue> ValidationIssues { get; private set; } = Array.Empty<ReportContentValidationIssue>();
    public bool SaveConflict { get; private set; }
    public string? SaveStatus { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct = default)
    {
        SetPrivateHeaders();
        var source = await FindSubmittedResponseAsync(AssessmentId, SubmittedResponseId, ct);
        if (source is null || !IsOwnerAllowed(source.Assessment))
        {
            return NotFound();
        }

        PopulateIdentity(source.Assessment, source.Response.SubmittedUtc!.Value);
        var reportResult = await _reportService.GetBySubmittedResponseIdAsync(SubmittedResponseId, User, ct);
        if (reportResult.State == AssessmentReportServiceState.Forbidden)
        {
            return NotFound();
        }
        if (reportResult.State == AssessmentReportServiceState.Retrieved && reportResult.Report is { } report)
        {
            var revision = report.Revisions.OrderByDescending(item => item.RevisionNumber).FirstOrDefault();
            if (revision is null)
            {
                return NotFound();
            }

            RevisionNumber = revision.RevisionNumber;
            RevisionStatus = revision.Status;
            RevisionId = revision.Id;
            var contentResult = await _contentService.GetAsync(revision.Id, User, ct);
            if (!contentResult.Succeeded)
            {
                return contentResult.State == AssessmentReportRevisionContentState.Forbidden ? NotFound() : StatusCode(500);
            }
            ReportContent = contentResult.Content;
            LastSavedUtc = contentResult.UpdatedUtc;
            ExpectedConcurrencyStamp = contentResult.ConcurrencyStamp ?? Guid.Empty;
            if (ReportContent is not null)
            {
                Input = ExecutiveSummaryInput.From(ReportContent.ExecutiveSummary);
                IndustrialInput = IndustrialDataInput.From(ReportContent.IndustrialDataAndSystems);
            }
            Scoring = CreateScoringView(revision.ScoringSnapshot, revision.ScoringSnapshotSchemaVersion);
        }
        else if (reportResult.State != AssessmentReportServiceState.NotFound)
        {
            return NotFound();
        }

        PopulateQuestionnaire(source.Assessment.QuestionnaireVersion, source.Response.Data);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveIndustrialDataAsync(CancellationToken ct = default)
    {
        SetPrivateHeaders();
        var source = await FindSubmittedResponseAsync(AssessmentId, SubmittedResponseId, ct);
        if (source is null || !IsOwnerAllowed(source.Assessment))
        {
            return NotFound();
        }
        PopulateIdentity(source.Assessment, source.Response.SubmittedUtc!.Value);
        PopulateQuestionnaire(source.Assessment.QuestionnaireVersion, source.Response.Data);
        var postedConcurrencyStamp = ExpectedConcurrencyStamp;

        var reportResult = await _reportService.GetBySubmittedResponseIdAsync(SubmittedResponseId, User, ct);
        if (reportResult.State != AssessmentReportServiceState.Retrieved || reportResult.Report is not { } report)
        {
            return NotFound();
        }
        var revision = report.Revisions.SingleOrDefault(item => item.Id == RevisionId);
        if (revision is null)
        {
            return NotFound();
        }
        RevisionId = revision.Id;
        RevisionNumber = revision.RevisionNumber;
        RevisionStatus = revision.Status;
        Scoring = CreateScoringView(revision.ScoringSnapshot, revision.ScoringSnapshotSchemaVersion);

        var retrieved = await _contentService.GetAsync(revision.Id, User, ct);
        if (!retrieved.Succeeded || retrieved.Content is null)
        {
            return retrieved.State == AssessmentReportRevisionContentState.Forbidden ? NotFound() : StatusCode(500);
        }
        LastSavedUtc = retrieved.UpdatedUtc;
        ReportContent = retrieved.Content;
        if (revision.Status != AssessmentReportRevisionStatus.Draft)
        {
            ModelState.AddModelError(string.Empty, "Only Draft report revisions can be edited.");
            return Page();
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }
        if (retrieved.ConcurrencyStamp != postedConcurrencyStamp)
        {
            SaveConflict = true;
            ModelState.AddModelError(string.Empty, "This Draft changed after you opened it. Your system inventory has been kept; reload the report before saving again.");
            return Page();
        }

        var existingSection = retrieved.Content.IndustrialDataAndSystems;
        var existingSystems = existingSection.Systems ?? Array.Empty<SystemAssessmentEntry>();
        var submittedIds = new HashSet<string>(StringComparer.Ordinal);
        var mergedSystems = new List<SystemAssessmentEntry>();
        for (var index = 0; index < IndustrialInput.Systems.Count; index++)
        {
            var input = IndustrialInput.Systems[index];
            var generatedForNewSystem = string.IsNullOrWhiteSpace(input.Id);
            if (string.IsNullOrWhiteSpace(input.Id))
            {
                input.Id = Guid.NewGuid().ToString("N");
            }
            var existingSystem = existingSystems.FirstOrDefault(system => string.Equals(system.Id, input.Id, StringComparison.Ordinal));
            if (existingSystem is null && !generatedForNewSystem && !Guid.TryParse(input.Id, out _))
            {
                ModelState.AddModelError($"IndustrialInput.Systems[{index}].Id", "System ID is invalid. Reload the Draft and retry the edit.");
                continue;
            }
            if (!submittedIds.Add(input.Id))
            {
                ModelState.AddModelError($"IndustrialInput.Systems[{index}].Id", "System IDs must be unique.");
                continue;
            }
            if (existingSystem is not null)
            {
                mergedSystems.Add(existingSystem with
                {
                    Name = input.Name,
                    AssessmentState = input.AssessmentState,
                    SystemType = input.SystemType,
                    Owner = input.Owner,
                    OperationalArea = input.OperationalArea,
                    DataExchanged = input.DataExchanged,
                    IntegrationPattern = input.IntegrationPattern,
                    Criticality = input.Criticality,
                    ConsultantNotes = input.ConsultantNotes
                });
            }
            else
            {
                mergedSystems.Add(new SystemAssessmentEntry(
                    input.Id,
                    input.Name,
                    input.AssessmentState,
                    input.SystemType,
                    input.Owner,
                    input.OperationalArea,
                    input.DataExchanged,
                    input.IntegrationPattern,
                    input.Criticality,
                    ReportContentProvenance.ConsultantEntered,
                    ReportVerificationStatus.Unverified,
                    Array.Empty<ReportEvidenceReference>(),
                    input.ConsultantNotes));
            }
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }
        mergedSystems.AddRange(existingSystems.Where(system => !submittedIds.Contains(system.Id)));
        var updatedSection = existingSection with
        {
            State = IndustrialInput.State,
            Systems = mergedSystems
        };
        var updatedContent = retrieved.Content with { IndustrialDataAndSystems = updatedSection };
        var saved = await _contentService.SaveDraftAsync(revision.Id, postedConcurrencyStamp, updatedContent, User, ct);
        ValidationIssues = saved.ValidationIssues ?? Array.Empty<ReportContentValidationIssue>();
        if (saved.State == AssessmentReportRevisionContentState.Saved)
        {
            ReportContent = saved.Content;
            ExpectedConcurrencyStamp = saved.ConcurrencyStamp ?? ExpectedConcurrencyStamp;
            LastSavedUtc = saved.UpdatedUtc;
            IndustrialInput = IndustrialDataInput.From(saved.Content!.IndustrialDataAndSystems);
            SaveStatus = "Industrial Data & Systems Draft saved.";
            return Page();
        }
        if (saved.State == AssessmentReportRevisionContentState.ValidationFailed)
        {
            for (var index = 0; index < IndustrialInput.Systems.Count; index++)
            {
                if (string.IsNullOrWhiteSpace(IndustrialInput.Systems[index].Id))
                {
                    IndustrialInput.Systems[index].Id = Guid.NewGuid().ToString("N");
                    ModelState.Remove($"IndustrialInput.Systems[{index}].Id");
                }
            }
        }
        if (saved.State == AssessmentReportRevisionContentState.ConcurrencyConflict)
        {
            SaveConflict = true;
            ModelState.AddModelError(string.Empty, "This Draft was changed by another editor. Your system inventory has been kept and was not saved; reload to review the newer version.");
        }
        else if (saved.State == AssessmentReportRevisionContentState.NotDraft)
        {
            ModelState.AddModelError(string.Empty, "Only Draft report revisions can be edited.");
        }
        else if (saved.State == AssessmentReportRevisionContentState.ValidationFailed)
        {
            ModelState.AddModelError(string.Empty, "Review the validation issues below. Your entered system inventory has been kept.");
        }
        else
        {
            ModelState.AddModelError(string.Empty, "Draft could not be saved. Your entered system inventory has been kept.");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveExecutiveSummaryAsync(CancellationToken ct = default)
    {
        SetPrivateHeaders();
        var source = await FindSubmittedResponseAsync(AssessmentId, SubmittedResponseId, ct);
        if (source is null || !IsOwnerAllowed(source.Assessment))
        {
            return NotFound();
        }
        PopulateIdentity(source.Assessment, source.Response.SubmittedUtc!.Value);
        PopulateQuestionnaire(source.Assessment.QuestionnaireVersion, source.Response.Data);
        EvidenceOptions = QuestionnaireSections.SelectMany(section => section.Answers).ToArray();
        var postedConcurrencyStamp = ExpectedConcurrencyStamp;

        var reportResult = await _reportService.GetBySubmittedResponseIdAsync(SubmittedResponseId, User, ct);
        if (reportResult.State == AssessmentReportServiceState.Forbidden)
        {
            return NotFound();
        }
        if (reportResult.State != AssessmentReportServiceState.Retrieved || reportResult.Report is not { } report)
        {
            return NotFound();
        }
        var revision = report.Revisions.SingleOrDefault(item => item.Id == RevisionId);
        if (revision is null)
        {
            return NotFound();
        }
        RevisionId = revision.Id;
        RevisionNumber = revision.RevisionNumber;
        RevisionStatus = revision.Status;
        Scoring = CreateScoringView(revision.ScoringSnapshot, revision.ScoringSnapshotSchemaVersion);

        var retrieved = await _contentService.GetAsync(revision.Id, User, ct);
        if (!retrieved.Succeeded || retrieved.Content is null)
        {
            return retrieved.State == AssessmentReportRevisionContentState.Forbidden ? NotFound() : StatusCode(500);
        }
        LastSavedUtc = retrieved.UpdatedUtc;
        ReportContent = retrieved.Content;
        if (revision.Status != AssessmentReportRevisionStatus.Draft)
        {
            ModelState.AddModelError(string.Empty, "Only Draft report revisions can be edited.");
            return Page();
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }
        if (retrieved.ConcurrencyStamp != postedConcurrencyStamp)
        {
            SaveConflict = true;
            ModelState.AddModelError(string.Empty, "This Draft changed after you opened it. Your text has been kept; reload the report before saving again.");
            return Page();
        }

        var existing = retrieved.Content.ExecutiveSummary;
        var selectedQuestionIds = (Input.EvidenceQuestionIds ?? new List<string>()).ToHashSet(StringComparer.Ordinal);
        var preservedReferences = existing.EvidenceReferences?.Where(reference => reference.Kind != ReportEvidenceKind.QuestionnaireAnswer)
            ?? Array.Empty<ReportEvidenceReference>();
        var questionnaireReferences = selectedQuestionIds.Select(questionId => new ReportEvidenceReference(
            ReportEvidenceKind.QuestionnaireAnswer, questionId, QuestionId: questionId));
        var updatedSummary = existing with
        {
            State = Input.State,
            Narrative = Input.Narrative,
            EvidenceReferences = preservedReferences.Concat(questionnaireReferences).ToArray()
        };
        var updatedContent = retrieved.Content with { ExecutiveSummary = updatedSummary };
        var saved = await _contentService.SaveDraftAsync(revision.Id, postedConcurrencyStamp, updatedContent, User, ct);
        ValidationIssues = saved.ValidationIssues ?? Array.Empty<ReportContentValidationIssue>();
        if (saved.State == AssessmentReportRevisionContentState.Saved)
        {
            ReportContent = saved.Content;
            ExpectedConcurrencyStamp = saved.ConcurrencyStamp ?? ExpectedConcurrencyStamp;
            LastSavedUtc = saved.UpdatedUtc;
            Input = ExecutiveSummaryInput.From(saved.Content!.ExecutiveSummary);
            SaveStatus = "Executive Summary Draft saved.";
            return Page();
        }
        if (saved.State == AssessmentReportRevisionContentState.ConcurrencyConflict)
        {
            SaveConflict = true;
            ModelState.AddModelError(string.Empty, "This Draft was changed by another editor. Your text has been kept and was not saved; reload to review the newer version.");
        }
        else if (saved.State == AssessmentReportRevisionContentState.NotDraft)
        {
            ModelState.AddModelError(string.Empty, "Only Draft report revisions can be edited.");
        }
        else if (saved.State == AssessmentReportRevisionContentState.ValidationFailed)
        {
            ModelState.AddModelError(string.Empty, "Review the validation issues below. Your entered text has been kept.");
        }
        else
        {
            ModelState.AddModelError(string.Empty, "Draft could not be saved. Your entered text has been kept.");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostCreateDraftAsync(CancellationToken ct = default)
    {
        SetPrivateHeaders();
        var source = await FindSubmittedResponseAsync(AssessmentId, SubmittedResponseId, ct);
        if (source is null || source.Response.Id != SubmittedResponseId)
        {
            return NotFound();
        }
        if (!IsOwnerAllowed(source.Assessment))
        {
            return NotFound();
        }

        PopulateIdentity(source.Assessment, source.Response.SubmittedUtc!.Value);
        var result = await _reportService.CreateAsync(AssessmentId, SubmittedResponseId, User, ct);
        if (result.State is AssessmentReportServiceState.Created or AssessmentReportServiceState.AlreadyExists)
        {
            return RedirectToPage("/Assessments/Report", new { area = "Internal", assessmentId = AssessmentId, submittedResponseId = SubmittedResponseId });
        }
        if (result.State == AssessmentReportServiceState.Forbidden)
        {
            return NotFound();
        }

        ModelState.AddModelError(string.Empty, "A Draft report could not be opened for this submitted response. Refresh the assessment and try again.");
        CanCreateReport = true;
        PopulateQuestionnaire(source.Assessment.QuestionnaireVersion, source.Response.Data);
        return Page();
    }

    private async Task<SubmittedSource?> FindSubmittedResponseAsync(Guid assessmentId, Guid submittedResponseId, CancellationToken ct)
    {
        var assessment = await _db.Set<Assessment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == assessmentId, ct);
        if (assessment is null)
        {
            return null;
        }

        var response = await _db.Set<AssessmentResponse>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == submittedResponseId && item.AssessmentId == assessmentId && !item.IsDraft && item.SubmittedUtc != null, ct);
        return response is null ? null : new SubmittedSource(assessment, response);
    }

    private void PopulateIdentity(Assessment assessment, DateTime submittedUtc)
    {
        AssessmentTitle = assessment.Title ?? "Assessment";
        SiteName = assessment.SiteName ?? string.Empty;
        SubmittedUtc = submittedUtc;
        CanCreateReport = true;
    }

    private void PopulateQuestionnaire(string questionnaireVersion, string responseJson)
    {
        try
        {
            var definition = QuestionnaireDefinitionResolver.Resolve(questionnaireVersion);
            var response = QuestionnaireResponseV1Serializer.Deserialize(responseJson);
            if (response is null || !string.Equals(response.SchemaVersion, definition.SchemaVersion, StringComparison.Ordinal))
            {
                QuestionnaireSections = Array.Empty<QuestionnaireSectionView>();
                return;
            }
            var sanitized = QuestionnaireResponseV1Sanitizer.SanitizeForClient(definition, response);
            QuestionnaireSections = definition.Sections.Select(section => new QuestionnaireSectionView(
                section.Title,
                section.Questions
                    .Where(question => sanitized.Answers.ContainsKey(question.Id))
                    .Select(question => new QuestionnaireAnswerView(
                        question.Id,
                        question.DisplayNumber,
                        question.Text,
                        FormatAnswer(question, sanitized.Answers[question.Id])))
                    .ToArray())).Where(section => section.Answers.Count > 0).ToArray();
            EvidenceOptions = QuestionnaireSections.SelectMany(section => section.Answers).ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or System.Text.Json.JsonException or NotSupportedException)
        {
            QuestionnaireSections = Array.Empty<QuestionnaireSectionView>();
        }
    }

    private static string FormatAnswer(QuestionDefinition question, QuestionAnswer answer)
    {
        if (answer.Text is not null)
        {
            return answer.Text;
        }
        if (answer.Selected is not null)
        {
            return ChoiceLabel(question.Choices, answer.Selected);
        }
        if (answer.SelectedValues is not null)
        {
            return string.Join(", ", answer.SelectedValues.Select(value => ChoiceLabel(question.Choices, value)));
        }

        var fields = question.RepeatableFields ?? Array.Empty<FieldDefinition>();
        var values = new List<string>();
        if (answer.Composite is not null)
        {
            values.AddRange(FormatFields(fields, answer.Composite));
        }
        if (answer.Repeatable is not null)
        {
            foreach (var record in answer.Repeatable)
            {
                values.Add(string.Join("; ", FormatFields(fields, record)));
            }
        }
        if (answer.Supplemental is not null)
        {
            var supplementalLabels = (question.SupplementalFields ?? Array.Empty<SupplementalFieldDefinition>())
                .ToDictionary(field => field.Id, field => field.Label, StringComparer.Ordinal);
            values.AddRange(answer.Supplemental.Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .Select(item => $"{(supplementalLabels.TryGetValue(item.Key, out var label) ? label : item.Key)}: {item.Value}"));
        }
        return string.Join("; ", values);
    }

    private static IEnumerable<string> FormatFields(FieldDefinition[] definitions, IReadOnlyDictionary<string, FieldAnswer> answers)
    {
        foreach (var item in answers)
        {
            var field = definitions.FirstOrDefault(definition => definition.Id == item.Key);
            if (field is null)
            {
                continue;
            }
            var answer = item.Value.Text ?? item.Value.Selected ??
                (item.Value.SelectedValues is null ? null : string.Join(", ", item.Value.SelectedValues));
            if (!string.IsNullOrWhiteSpace(answer))
            {
                yield return $"{field.Label}: {(item.Value.Text is null ? ChoiceLabel(field.Choices, answer) : answer)}";
            }
        }
    }

    private static string ChoiceLabel(IReadOnlyCollection<Choice>? choices, string value)
        => choices?.FirstOrDefault(choice => choice.Value == value)?.Label ?? value;

    private static ReadinessScoreView? CreateScoringView(string? json, int? schemaVersion)
    {
        if (json is null || schemaVersion != ReadinessScoringSnapshotSerializer.PersistedSchemaVersion)
        {
            return null;
        }
        try
        {
            var snapshot = ReadinessScoringSnapshotSerializer.Deserialize(json).Snapshot;
            return new ReadinessScoreView(
                snapshot.OverallStatus.ToString(),
                snapshot.OverallDisplayScore0To100,
                snapshot.OverallClassification?.ToString(),
                snapshot.WithholdingReasons.Select(reason => reason.ToString()).ToArray(),
                snapshot.Dimensions.Select(dimension => new ReadinessDimensionView(
                    dimension.Name,
                    dimension.Status.ToString(),
                    dimension.DisplayScore0To100,
                    dimension.Classification?.ToString(),
                    dimension.ScoreableItemCount,
                    dimension.TotalEligibleItemCount,
                    dimension.WithholdingReasons.Select(reason => reason.ToString()).ToArray())).ToArray());
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void SetPrivateHeaders()
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";
    }

    private bool IsOwnerAllowed(Assessment assessment)
    {
        var callerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(assessment.OwnerUserId) ||
            string.Equals(assessment.OwnerUserId, callerId, StringComparison.Ordinal);
    }

    private sealed record SubmittedSource(Assessment Assessment, AssessmentResponse Response);
    public sealed class ExecutiveSummaryInput
    {
        public ReportAssessmentState State { get; set; } = ReportAssessmentState.NotAssessed;
        public string? Narrative { get; set; }
        public List<string>? EvidenceQuestionIds { get; set; } = new();

        public static ExecutiveSummaryInput From(ExecutiveSummarySection section) => new()
        {
            State = section.State,
            Narrative = section.Narrative,
            EvidenceQuestionIds = section.EvidenceReferences?
                .Where(reference => reference.Kind == ReportEvidenceKind.QuestionnaireAnswer && reference.QuestionId is not null)
                .Select(reference => reference.QuestionId!)
                .ToList() ?? new List<string>()
        };
    }

    public sealed class IndustrialDataInput
    {
        public ReportAssessmentState State { get; set; } = ReportAssessmentState.NotAssessed;
        public List<SystemInventoryInput> Systems { get; set; } = new();

        public static IndustrialDataInput From(IndustrialDataAndSystemsSection section) => new()
        {
            State = section.State,
            Systems = section.Systems?.Select(SystemInventoryInput.From).ToList() ?? new List<SystemInventoryInput>()
        };
    }

    public sealed class SystemInventoryInput
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public ReportAssessmentState AssessmentState { get; set; } = ReportAssessmentState.NotAssessed;
        public string? SystemType { get; set; }
        public string? Owner { get; set; }
        public string? OperationalArea { get; set; }
        public string? DataExchanged { get; set; }
        public string? IntegrationPattern { get; set; }
        public string? Criticality { get; set; }
        public string? ConsultantNotes { get; set; }

        public static SystemInventoryInput From(SystemAssessmentEntry system) => new()
        {
            Id = system.Id,
            Name = system.Name,
            AssessmentState = system.AssessmentState,
            SystemType = system.SystemType,
            Owner = system.Owner,
            OperationalArea = system.OperationalArea,
            DataExchanged = system.DataExchanged,
            IntegrationPattern = system.IntegrationPattern,
            Criticality = system.Criticality,
            ConsultantNotes = system.ConsultantNotes
        };
    }
    public sealed record QuestionnaireSectionView(string Title, IReadOnlyList<QuestionnaireAnswerView> Answers);
    public sealed record QuestionnaireAnswerView(string QuestionId, string Number, string Question, string Answer);
    public sealed record ReadinessScoreView(string OverallStatus, int? OverallScore, string? Classification, IReadOnlyList<string> WithholdingReasons, IReadOnlyList<ReadinessDimensionView> Dimensions);
    public sealed record ReadinessDimensionView(string Name, string Status, int? Score, string? Classification, int ScoreableItems, int TotalItems, IReadOnlyList<string> WithholdingReasons);
}
