using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;
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
    public AiOpportunitiesInput AiInput { get; set; } = new();

    [BindProperty]
    public TechnicalGapsInput TechnicalInput { get; set; } = new();

    private bool _aiInputHandled;
    private bool _technicalInputHandled;
    private const int MaxListItems = 50;
    private const int MaxListItemLength = 500;
    private const decimal MaxEstimateAmount = 1_000_000_000_000m;

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
        var offeredQuestionIds = EvidenceOptions.Select(option => option.QuestionId).ToHashSet(StringComparer.Ordinal);
        var existingFindings = existingSection.Findings ?? Array.Empty<ReportFinding>();
        var submittedFindingIds = new HashSet<string>(StringComparer.Ordinal);
        var mergedFindings = new List<ReportFinding>();
        for (var index = 0; index < IndustrialInput.Findings.Count; index++)
        {
            var input = IndustrialInput.Findings[index];
            var generatedForNewFinding = string.IsNullOrWhiteSpace(input.Id);
            if (generatedForNewFinding)
            {
                input.Id = Guid.NewGuid().ToString("N");
                ModelState.Remove($"IndustrialInput.Findings[{index}].Id");
            }
            var existingFinding = existingFindings.FirstOrDefault(finding => string.Equals(finding.Id, input.Id, StringComparison.Ordinal));
            if (existingFinding is null && !generatedForNewFinding && !Guid.TryParse(input.Id, out _))
            {
                ModelState.AddModelError($"IndustrialInput.Findings[{index}].Id", "Finding ID is invalid. Reload the Draft and retry the edit.");
                continue;
            }
            if (!submittedFindingIds.Add(input.Id!))
            {
                ModelState.AddModelError($"IndustrialInput.Findings[{index}].Id", "Finding IDs must be unique.");
                continue;
            }
            var findingEvidence = MergeQuestionnaireEvidence(existingFinding?.EvidenceReferences, input.EvidenceQuestionIds, offeredQuestionIds);
            mergedFindings.Add(existingFinding is not null
                ? existingFinding with
                {
                    Title = input.Title,
                    Narrative = input.Narrative,
                    Classification = input.Classification,
                    Provenance = input.Provenance,
                    VerificationStatus = input.VerificationStatus,
                    ConsultantNotes = input.ConsultantNotes,
                    EvidenceReferences = findingEvidence
                }
                : new ReportFinding(
                    input.Id!,
                    input.Title,
                    input.Narrative,
                    input.Classification,
                    input.Provenance,
                    input.VerificationStatus,
                    findingEvidence,
                    input.ConsultantNotes));
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }
        mergedSystems.AddRange(existingSystems.Where(system => !submittedIds.Contains(system.Id)));
        mergedFindings.AddRange(existingFindings.Where(finding => !submittedFindingIds.Contains(finding.Id)));
        var sectionEvidence = MergeQuestionnaireEvidence(existingSection.EvidenceReferences, IndustrialInput.EvidenceQuestionIds, offeredQuestionIds);
        var updatedSection = existingSection with
        {
            State = IndustrialInput.State,
            Systems = mergedSystems,
            Findings = mergedFindings.Count == 0 && existingSection.Findings is null ? null : mergedFindings,
            EvidenceReferences = sectionEvidence.Count == 0 && existingSection.EvidenceReferences is null ? null : sectionEvidence
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

    public override void OnPageHandlerExecuted(PageHandlerExecutedContext context)
    {
        if (!_aiInputHandled && ReportContent is not null)
        {
            AiInput = AiOpportunitiesInput.From(ReportContent.AiOpportunitiesAndRoi);
        }
        if (!_technicalInputHandled && ReportContent is not null)
        {
            TechnicalInput = TechnicalGapsInput.From(ReportContent.TechnicalGapsAndArchitecture);
        }
        base.OnPageHandlerExecuted(context);
    }

    public async Task<IActionResult> OnPostSaveTechnicalGapsAsync(CancellationToken ct = default)
    {
        _technicalInputHandled = true;
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
            ModelState.AddModelError(string.Empty, "This Draft changed after you opened it. Your technical gaps and recommendations have been kept; reload the report before saving again.");
            return Page();
        }

        var offeredQuestionIds = EvidenceOptions.Select(option => option.QuestionId).ToHashSet(StringComparer.Ordinal);
        var existingSection = retrieved.Content.TechnicalGapsAndArchitecture;
        var storedGaps = existingSection.Gaps ?? Array.Empty<TechnicalGapEntry>();
        var storedRecommendations = existingSection.Recommendations ?? Array.Empty<ArchitectureRecommendation>();
        var storedGapIds = storedGaps.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var storedRecommendationIds = storedRecommendations.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var submittedGapIds = new HashSet<string>(StringComparer.Ordinal);
        var submittedRecommendationIds = new HashSet<string>(StringComparer.Ordinal);
        var mergedGaps = new List<TechnicalGapEntry>();
        var mergedRecommendations = new List<ArchitectureRecommendation>();

        for (var index = 0; index < TechnicalInput.Gaps.Count; index++)
        {
            var input = TechnicalInput.Gaps[index];
            var id = ResolveItemId(input.Id, storedGapIds, submittedGapIds, $"TechnicalInput.Gaps[{index}].Id", $"Gap {index + 1}");
            if (id is null)
            {
                continue;
            }
            input.Id = id;
            var existing = storedGaps.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            var evidence = MergeQuestionnaireEvidence(existing?.EvidenceReferences, input.EvidenceQuestionIds, offeredQuestionIds);
            mergedGaps.Add(new TechnicalGapEntry(
                id,
                input.Title?.Trim() ?? existing?.Title ?? string.Empty,
                input.Description?.Trim() ?? existing?.Description ?? string.Empty,
                input.Impact?.Trim() ?? existing?.Impact ?? string.Empty,
                input.Provenance ?? existing?.Provenance ?? ReportContentProvenance.ConsultantEntered,
                input.VerificationStatus ?? existing?.VerificationStatus ?? ReportVerificationStatus.Unverified,
                evidence,
                input.RequiresValidation ?? existing?.RequiresValidation ?? false));
        }

        for (var index = 0; index < TechnicalInput.Recommendations.Count; index++)
        {
            var input = TechnicalInput.Recommendations[index];
            var id = ResolveItemId(input.Id, storedRecommendationIds, submittedRecommendationIds, $"TechnicalInput.Recommendations[{index}].Id", $"Recommendation {index + 1}");
            if (id is null)
            {
                continue;
            }
            input.Id = id;
            var existing = storedRecommendations.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            var evidence = MergeQuestionnaireEvidence(existing?.EvidenceReferences, input.EvidenceQuestionIds, offeredQuestionIds);
            mergedRecommendations.Add(new ArchitectureRecommendation(
                id,
                input.Title?.Trim() ?? existing?.Title ?? string.Empty,
                input.Recommendation?.Trim() ?? existing?.Recommendation ?? string.Empty,
                input.Rationale?.Trim() ?? existing?.Rationale ?? string.Empty,
                input.Provenance ?? existing?.Provenance ?? ReportContentProvenance.ConsultantEntered,
                input.VerificationStatus ?? existing?.VerificationStatus ?? ReportVerificationStatus.Unverified,
                evidence,
                input.RequiresValidation ?? existing?.RequiresValidation ?? false));
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }

        mergedGaps.AddRange(storedGaps.Where(item => !submittedGapIds.Contains(item.Id)));
        mergedRecommendations.AddRange(storedRecommendations.Where(item => !submittedRecommendationIds.Contains(item.Id)));
        var updatedSection = existingSection with
        {
            State = TechnicalInput.State,
            Gaps = mergedGaps.Count == 0 && existingSection.Gaps is null ? null : mergedGaps,
            Recommendations = mergedRecommendations.Count == 0 && existingSection.Recommendations is null ? null : mergedRecommendations
        };
        var updatedContent = retrieved.Content with { TechnicalGapsAndArchitecture = updatedSection };
        var saved = await _contentService.SaveDraftAsync(revision.Id, postedConcurrencyStamp, updatedContent, User, ct);
        ValidationIssues = saved.ValidationIssues ?? Array.Empty<ReportContentValidationIssue>();
        if (saved.State == AssessmentReportRevisionContentState.Saved)
        {
            ReportContent = saved.Content;
            ExpectedConcurrencyStamp = saved.ConcurrencyStamp ?? ExpectedConcurrencyStamp;
            LastSavedUtc = saved.UpdatedUtc;
            TechnicalInput = TechnicalGapsInput.From(saved.Content!.TechnicalGapsAndArchitecture);
            SaveStatus = "Technical Gaps & Architecture Draft saved.";
            return Page();
        }
        if (saved.State == AssessmentReportRevisionContentState.ConcurrencyConflict)
        {
            SaveConflict = true;
            ModelState.AddModelError(string.Empty, "This Draft was changed by another editor. Your technical gaps and recommendations have been kept and were not saved; reload to review the newer version.");
        }
        else if (saved.State == AssessmentReportRevisionContentState.NotDraft)
        {
            ModelState.AddModelError(string.Empty, "Only Draft report revisions can be edited.");
        }
        else if (saved.State == AssessmentReportRevisionContentState.ValidationFailed)
        {
            ModelState.AddModelError(string.Empty, "Review the validation issues below. Your entered technical gaps and recommendations have been kept.");
        }
        else
        {
            ModelState.AddModelError(string.Empty, "Draft could not be saved. Your entered technical gaps and recommendations have been kept.");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAiOpportunitiesAsync(CancellationToken ct = default)
    {
        _aiInputHandled = true;
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
            ModelState.AddModelError(string.Empty, "This Draft changed after you opened it. Your opportunities have been kept; reload the report before saving again.");
            return Page();
        }

        var offeredQuestionIds = EvidenceOptions.Select(option => option.QuestionId).ToHashSet(StringComparer.Ordinal);
        var existingSection = retrieved.Content.AiOpportunitiesAndRoi;
        var existingOpportunities = existingSection.Opportunities ?? Array.Empty<AiImplementationOpportunity>();
        var storedOpportunityIds = existingOpportunities.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var submittedOpportunityIds = new HashSet<string>(StringComparer.Ordinal);
        var mergedOpportunities = new List<AiImplementationOpportunity>();
        for (var index = 0; index < AiInput.Opportunities.Count; index++)
        {
            var input = AiInput.Opportunities[index];
            var key = $"AiInput.Opportunities[{index}]";
            var label = $"Opportunity {index + 1}";
            var id = ResolveItemId(input.Id, storedOpportunityIds, submittedOpportunityIds, key + ".Id", label);
            if (id is null)
            {
                continue;
            }
            input.Id = id;
            var existing = existingOpportunities.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            var dataSources = ParseLines(input.RequiredDataSourcesText, key + ".RequiredDataSourcesText", label + " required data sources") ?? existing?.RequiredDataSources ?? Array.Empty<string>();
            var prerequisites = ParseLines(input.TechnicalPrerequisitesText, key + ".TechnicalPrerequisitesText", label + " technical prerequisites") ?? existing?.TechnicalPrerequisites ?? Array.Empty<string>();
            var dependencies = ParseLines(input.ImplementationDependenciesText, key + ".ImplementationDependenciesText", label + " implementation dependencies") ?? existing?.ImplementationDependencies ?? Array.Empty<string>();
            var risks = ParseLines(input.RisksAndAssumptionsText, key + ".RisksAndAssumptionsText", label + " risks and assumptions") ?? existing?.RisksAndAssumptions ?? Array.Empty<string>();
            var scope = BuildScope(input, existing?.Scope, key, label);
            var estimate = BuildEstimate(input, existing?.Estimate, key, label, offeredQuestionIds);
            var evidence = MergeQuestionnaireEvidence(existing?.EvidenceReferences, input.EvidenceQuestionIds, offeredQuestionIds);
            var provenance = input.Provenance ?? existing?.Provenance ?? ReportContentProvenance.ConsultantEntered;
            var verification = input.VerificationStatus ?? existing?.VerificationStatus ?? ReportVerificationStatus.Unverified;
            mergedOpportunities.Add(new AiImplementationOpportunity(
                id,
                input.BusinessProblem?.Trim() ?? string.Empty,
                input.OperationalProcessOrArea?.Trim() ?? string.Empty,
                input.ProposedUseCase?.Trim() ?? string.Empty,
                Trimmed(input.ExpectedMeasurableOutcome),
                dataSources,
                prerequisites,
                dependencies,
                risks,
                estimate,
                scope,
                provenance,
                verification,
                evidence));
        }

        var storedFindings = existingSection.Findings ?? Array.Empty<ReportFinding>();
        var submittedFindingIds = new HashSet<string>(StringComparer.Ordinal);
        var mergedFindings = MergeFindingInputs(storedFindings, AiInput.Findings, offeredQuestionIds, "AiInput.Findings", submittedFindingIds);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        mergedOpportunities.AddRange(existingOpportunities.Where(item => !submittedOpportunityIds.Contains(item.Id)));
        mergedFindings.AddRange(storedFindings.Where(item => !submittedFindingIds.Contains(item.Id)));
        var updatedSection = existingSection with
        {
            State = AiInput.State,
            Opportunities = mergedOpportunities.Count == 0 && existingSection.Opportunities is null ? null : mergedOpportunities,
            Findings = mergedFindings.Count == 0 && existingSection.Findings is null ? null : mergedFindings
        };
        var updatedContent = retrieved.Content with { AiOpportunitiesAndRoi = updatedSection };
        var saved = await _contentService.SaveDraftAsync(revision.Id, postedConcurrencyStamp, updatedContent, User, ct);
        ValidationIssues = saved.ValidationIssues ?? Array.Empty<ReportContentValidationIssue>();
        if (saved.State == AssessmentReportRevisionContentState.Saved)
        {
            ReportContent = saved.Content;
            ExpectedConcurrencyStamp = saved.ConcurrencyStamp ?? ExpectedConcurrencyStamp;
            LastSavedUtc = saved.UpdatedUtc;
            AiInput = AiOpportunitiesInput.From(saved.Content!.AiOpportunitiesAndRoi);
            SaveStatus = "AI Opportunities & ROI Draft saved.";
            return Page();
        }
        if (saved.State == AssessmentReportRevisionContentState.ConcurrencyConflict)
        {
            SaveConflict = true;
            ModelState.AddModelError(string.Empty, "This Draft was changed by another editor. Your opportunities have been kept and were not saved; reload to review the newer version.");
        }
        else if (saved.State == AssessmentReportRevisionContentState.NotDraft)
        {
            ModelState.AddModelError(string.Empty, "Only Draft report revisions can be edited.");
        }
        else if (saved.State == AssessmentReportRevisionContentState.ValidationFailed)
        {
            ModelState.AddModelError(string.Empty, "Review the validation issues below. Your entered opportunities have been kept.");
        }
        else
        {
            ModelState.AddModelError(string.Empty, "Draft could not be saved. Your entered opportunities have been kept.");
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

    private static IReadOnlyList<ReportEvidenceReference> MergeQuestionnaireEvidence(
        IReadOnlyList<ReportEvidenceReference>? existing,
        IEnumerable<string>? selectedQuestionIds,
        ISet<string> offeredQuestionIds)
    {
        var selected = (selectedQuestionIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var remaining = selected.ToHashSet(StringComparer.Ordinal);
        var merged = new List<ReportEvidenceReference>();
        foreach (var reference in existing ?? Array.Empty<ReportEvidenceReference>())
        {
            if (reference.Kind == ReportEvidenceKind.QuestionnaireAnswer
                && reference.QuestionId is not null
                && offeredQuestionIds.Contains(reference.QuestionId))
            {
                if (remaining.Remove(reference.QuestionId))
                {
                    merged.Add(reference);
                }
                continue;
            }
            merged.Add(reference);
        }
        foreach (var questionId in selected.Where(remaining.Contains))
        {
            merged.Add(new ReportEvidenceReference(ReportEvidenceKind.QuestionnaireAnswer, questionId, QuestionId: questionId));
        }
        return merged;
    }

    private string? ResolveItemId(string? postedId, IReadOnlyCollection<string> storedIds, ISet<string> seen, string key, string label)
    {
        var generated = string.IsNullOrWhiteSpace(postedId);
        var id = generated ? Guid.NewGuid().ToString("N") : postedId!;
        if (!generated && !storedIds.Contains(id) && !Guid.TryParse(id, out _))
        {
            ModelState.AddModelError(key, $"{label}: ID is invalid. Reload the Draft and retry the edit.");
            return null;
        }
        if (!seen.Add(id))
        {
            ModelState.AddModelError(key, $"{label}: IDs must be unique.");
            return null;
        }
        return id;
    }

    private List<ReportFinding> MergeFindingInputs(
        IReadOnlyList<ReportFinding> stored,
        List<FindingInput> inputs,
        ISet<string> offeredQuestionIds,
        string keyPrefix,
        ISet<string> submittedIds)
    {
        var storedIds = stored.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var merged = new List<ReportFinding>();
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var id = ResolveItemId(input.Id, storedIds, submittedIds, $"{keyPrefix}[{index}].Id", $"Finding {index + 1}");
            if (id is null)
            {
                continue;
            }
            input.Id = id;
            var existing = stored.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            var evidence = MergeQuestionnaireEvidence(existing?.EvidenceReferences, input.EvidenceQuestionIds, offeredQuestionIds);
            merged.Add(existing is not null
                ? existing with
                {
                    Title = input.Title,
                    Narrative = input.Narrative,
                    Classification = input.Classification,
                    Provenance = input.Provenance,
                    VerificationStatus = input.VerificationStatus,
                    ConsultantNotes = input.ConsultantNotes,
                    EvidenceReferences = evidence
                }
                : new ReportFinding(id, input.Title, input.Narrative, input.Classification, input.Provenance, input.VerificationStatus, evidence, input.ConsultantNotes));
        }
        return merged;
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Amount(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    private IReadOnlyList<string>? ParseLines(string? text, string key, string label)
    {
        if (text is null)
        {
            return null;
        }
        var items = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line.Length > MaxListItemLength)
            {
                ModelState.AddModelError(key, $"{label}: each line must be {MaxListItemLength} characters or fewer (line {items.Count + 1} has {line.Length}).");
                continue;
            }
            items.Add(line);
        }
        if (items.Count > MaxListItems)
        {
            ModelState.AddModelError(key, $"{label}: no more than {MaxListItems} lines are allowed.");
        }
        return items;
    }

    private decimal? ParseAmount(string? text, string key, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            ModelState.AddModelError(key, $"{label} must be a plain number such as 1250 or 1250.50.");
            return null;
        }
        if (value < 0 || value > MaxEstimateAmount)
        {
            ModelState.AddModelError(key, $"{label} must be between 0 and {MaxEstimateAmount:N0}.");
            return null;
        }
        return value;
    }

    private ImplementationScope BuildScope(OpportunityInput input, ImplementationScope? existing, string key, string label)
    {
        if (input.ScopeState is not { } state)
        {
            return existing ?? new ImplementationScope(ReportAssessmentState.NotAssessed);
        }
        var description = input.ScopeDescription is null ? existing?.Description : Trimmed(input.ScopeDescription);
        var inScope = ParseLines(input.ScopeInText, key + ".ScopeInText", label + " in-scope items") ?? existing?.InScopeItems;
        var outOfScope = ParseLines(input.ScopeOutText, key + ".ScopeOutText", label + " out-of-scope items") ?? existing?.OutOfScopeItems;
        return new ImplementationScope(state, description, inScope, outOfScope);
    }

    private EffortCostEstimate BuildEstimate(OpportunityInput input, EffortCostEstimate? existing, string key, string label, ISet<string> offeredQuestionIds)
    {
        if (input.EstimateStatus is not { } status)
        {
            return existing ?? new EffortCostEstimate(ReportEstimateStatus.NotEstimated);
        }
        if (status == ReportEstimateStatus.NotEstimated && existing?.Status == ReportEstimateStatus.Estimated)
        {
            if (!input.ConfirmEstimateReset)
            {
                ModelState.AddModelError(key + ".ConfirmEstimateReset",
                    $"{label}: changing an existing estimate to Not estimated clears its effort, cost, basis, assumptions and evidence. Tick \"Confirm estimate reset\" to proceed, or keep the status as Estimated.");
                return existing;
            }
            return new EffortCostEstimate(ReportEstimateStatus.NotEstimated);
        }
        var estimateLabel = label + " estimate";
        var effort = input.EffortValue is null ? existing?.EffortValue : ParseAmount(input.EffortValue, key + ".EffortValue", estimateLabel + " effort value");
        var cost = input.CostAmount is null ? existing?.CostAmount : ParseAmount(input.CostAmount, key + ".CostAmount", estimateLabel + " cost amount");
        var unit = input.EffortUnit is null ? existing?.EffortUnit : Trimmed(input.EffortUnit);
        var currency = input.Currency is null ? existing?.Currency : Trimmed(input.Currency);
        var basis = input.EstimateBasis is null ? existing?.Basis : Trimmed(input.EstimateBasis);
        var assumptions = ParseLines(input.EstimateAssumptionsText, key + ".EstimateAssumptionsText", estimateLabel + " assumptions") ?? existing?.Assumptions;
        var evidence = MergeQuestionnaireEvidence(existing?.EvidenceReferences, input.EstimateEvidenceQuestionIds, offeredQuestionIds);
        return new EffortCostEstimate(
            status,
            effort,
            unit,
            cost,
            currency,
            basis,
            assumptions,
            evidence.Count == 0 && existing?.EvidenceReferences is null ? null : evidence);
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
        public List<FindingInput> Findings { get; set; } = new();
        public List<string>? EvidenceQuestionIds { get; set; } = new();

        public static IndustrialDataInput From(IndustrialDataAndSystemsSection section) => new()
        {
            State = section.State,
            Systems = section.Systems?.Select(SystemInventoryInput.From).ToList() ?? new List<SystemInventoryInput>(),
            Findings = section.Findings?.Select(FindingInput.From).ToList() ?? new List<FindingInput>(),
            EvidenceQuestionIds = QuestionIdsOf(section.EvidenceReferences)
        };
    }

    private static List<string> QuestionIdsOf(IReadOnlyList<ReportEvidenceReference>? references) =>
        references?
            .Where(reference => reference.Kind == ReportEvidenceKind.QuestionnaireAnswer && reference.QuestionId is not null)
            .Select(reference => reference.QuestionId!)
            .ToList() ?? new List<string>();

    public sealed class FindingInput
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Narrative { get; set; } = string.Empty;
        public ReportFindingClassification Classification { get; set; } = ReportFindingClassification.PreliminaryFinding;
        public ReportContentProvenance Provenance { get; set; } = ReportContentProvenance.ConsultantEntered;
        public ReportVerificationStatus VerificationStatus { get; set; } = ReportVerificationStatus.Unverified;
        public string? ConsultantNotes { get; set; }
        public List<string>? EvidenceQuestionIds { get; set; } = new();

        public static FindingInput From(ReportFinding finding) => new()
        {
            Id = finding.Id,
            Title = finding.Title,
            Narrative = finding.Narrative,
            Classification = finding.Classification,
            Provenance = finding.Provenance,
            VerificationStatus = finding.VerificationStatus,
            ConsultantNotes = finding.ConsultantNotes,
            EvidenceQuestionIds = QuestionIdsOf(finding.EvidenceReferences)
        };
    }

    public sealed class AiOpportunitiesInput
    {
        public ReportAssessmentState State { get; set; } = ReportAssessmentState.NotAssessed;
        public List<OpportunityInput> Opportunities { get; set; } = new();
        public List<FindingInput> Findings { get; set; } = new();

        public static AiOpportunitiesInput From(AiOpportunitiesAndRoiSection section) => new()
        {
            State = section.State,
            Opportunities = section.Opportunities?.Select(OpportunityInput.From).ToList() ?? new List<OpportunityInput>(),
            Findings = section.Findings?.Select(FindingInput.From).ToList() ?? new List<FindingInput>()
        };
    }

    public sealed class OpportunityInput
    {
        public string? Id { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? BusinessProblem { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? OperationalProcessOrArea { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? ProposedUseCase { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? ExpectedMeasurableOutcome { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? RequiredDataSourcesText { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? TechnicalPrerequisitesText { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? ImplementationDependenciesText { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? RisksAndAssumptionsText { get; set; }
        public ReportContentProvenance? Provenance { get; set; }
        public ReportVerificationStatus? VerificationStatus { get; set; }
        public List<string>? EvidenceQuestionIds { get; set; } = new();
        public ReportAssessmentState? ScopeState { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? ScopeDescription { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? ScopeInText { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? ScopeOutText { get; set; }
        public ReportEstimateStatus? EstimateStatus { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? EffortValue { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? EffortUnit { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? CostAmount { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? Currency { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? EstimateBasis { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? EstimateAssumptionsText { get; set; }
        public List<string>? EstimateEvidenceQuestionIds { get; set; } = new();
        public bool ConfirmEstimateReset { get; set; }

        private static string Lines(IReadOnlyList<string>? items) => items is null ? string.Empty : string.Join("\n", items);

        public static OpportunityInput From(AiImplementationOpportunity item) => new()
        {
            Id = item.Id,
            BusinessProblem = item.BusinessProblem,
            OperationalProcessOrArea = item.OperationalProcessOrArea,
            ProposedUseCase = item.ProposedUseCase,
            ExpectedMeasurableOutcome = item.ExpectedMeasurableOutcome ?? string.Empty,
            RequiredDataSourcesText = Lines(item.RequiredDataSources),
            TechnicalPrerequisitesText = Lines(item.TechnicalPrerequisites),
            ImplementationDependenciesText = Lines(item.ImplementationDependencies),
            RisksAndAssumptionsText = Lines(item.RisksAndAssumptions),
            Provenance = item.Provenance,
            VerificationStatus = item.VerificationStatus,
            EvidenceQuestionIds = QuestionIdsOf(item.EvidenceReferences),
            ScopeState = item.Scope.State,
            ScopeDescription = item.Scope.Description ?? string.Empty,
            ScopeInText = Lines(item.Scope.InScopeItems),
            ScopeOutText = Lines(item.Scope.OutOfScopeItems),
            EstimateStatus = item.Estimate.Status,
            EffortValue = Amount(item.Estimate.EffortValue) ?? string.Empty,
            EffortUnit = item.Estimate.EffortUnit ?? string.Empty,
            CostAmount = Amount(item.Estimate.CostAmount) ?? string.Empty,
            Currency = item.Estimate.Currency ?? string.Empty,
            EstimateBasis = item.Estimate.Basis ?? string.Empty,
            EstimateAssumptionsText = Lines(item.Estimate.Assumptions),
            EstimateEvidenceQuestionIds = QuestionIdsOf(item.Estimate.EvidenceReferences)
        };
    }

    public sealed class TechnicalGapsInput
    {
        public ReportAssessmentState State { get; set; } = ReportAssessmentState.NotAssessed;
        public List<TechnicalGapInput> Gaps { get; set; } = new();
        public List<ArchitectureRecommendationInput> Recommendations { get; set; } = new();

        public static TechnicalGapsInput From(TechnicalGapsAndArchitectureSection section) => new()
        {
            State = section.State,
            Gaps = section.Gaps?.Select(TechnicalGapInput.From).ToList() ?? new List<TechnicalGapInput>(),
            Recommendations = section.Recommendations?.Select(ArchitectureRecommendationInput.From).ToList() ?? new List<ArchitectureRecommendationInput>()
        };
    }

    public sealed class TechnicalGapInput
    {
        public string? Id { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? Title { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? Description { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? Impact { get; set; }
        public ReportContentProvenance? Provenance { get; set; }
        public ReportVerificationStatus? VerificationStatus { get; set; }
        public bool? RequiresValidation { get; set; }
        public List<string>? EvidenceQuestionIds { get; set; } = new();

        public static TechnicalGapInput From(TechnicalGapEntry item) => new()
        {
            Id = item.Id,
            Title = item.Title,
            Description = item.Description,
            Impact = item.Impact,
            Provenance = item.Provenance,
            VerificationStatus = item.VerificationStatus,
            RequiresValidation = item.RequiresValidation,
            EvidenceQuestionIds = QuestionIdsOf(item.EvidenceReferences)
        };
    }

    public sealed class ArchitectureRecommendationInput
    {
        public string? Id { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? Title { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? Recommendation { get; set; }
        [DisplayFormat(ConvertEmptyStringToNull = false)] public string? Rationale { get; set; }
        public ReportContentProvenance? Provenance { get; set; }
        public ReportVerificationStatus? VerificationStatus { get; set; }
        public bool? RequiresValidation { get; set; }
        public List<string>? EvidenceQuestionIds { get; set; } = new();

        public static ArchitectureRecommendationInput From(ArchitectureRecommendation item) => new()
        {
            Id = item.Id,
            Title = item.Title,
            Recommendation = item.Recommendation,
            Rationale = item.Rationale,
            Provenance = item.Provenance,
            VerificationStatus = item.VerificationStatus,
            RequiresValidation = item.RequiresValidation,
            EvidenceQuestionIds = QuestionIdsOf(item.EvidenceReferences)
        };
    }

    public sealed record TechnicalGapEditorModel(TechnicalGapInput Input, string Index, IReadOnlyList<QuestionnaireAnswerView> Options, TechnicalGapEntry? Stored);
    public sealed record ArchitectureRecommendationEditorModel(ArchitectureRecommendationInput Input, string Index, IReadOnlyList<QuestionnaireAnswerView> Options, ArchitectureRecommendation? Stored);
    public sealed record EvidenceCheckboxModel(IReadOnlyList<QuestionnaireAnswerView> Options, string FieldName, string IdPrefix, IReadOnlyCollection<string>? Selected);
    public sealed record AiOpportunityEditorModel(OpportunityInput Input, string Index, IReadOnlyList<QuestionnaireAnswerView> Options, AiImplementationOpportunity? Stored);
    public sealed record AiFindingEditorModel(FindingInput Input, string Index, IReadOnlyList<QuestionnaireAnswerView> Options, ReportFinding? Stored);

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
