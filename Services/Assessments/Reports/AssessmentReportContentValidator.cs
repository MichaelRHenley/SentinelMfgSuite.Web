using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports;

public sealed class AssessmentReportContentValidator : IAssessmentReportContentValidator
{
    private static readonly IReadOnlyDictionary<string, Func<ConsultantReportContent, ReportSectionState>> RequiredSections =
        new Dictionary<string, Func<ConsultantReportContent, ReportSectionState>>(StringComparer.Ordinal)
        {
            ["executiveSummary"] = content => new(content.ExecutiveSummary.State, content.ExecutiveSummary.Narrative, content.ExecutiveSummary.EvidenceReferences),
            ["industrialDataAndSystems"] = content => new(content.IndustrialDataAndSystems.State, null, content.IndustrialDataAndSystems.EvidenceReferences),
            ["aiOpportunitiesAndRoi"] = content => new(content.AiOpportunitiesAndRoi.State, null, null),
            ["technicalGapsAndArchitecture"] = content => new(content.TechnicalGapsAndArchitecture.State, null, null),
            ["implementationRoadmap"] = content => new(content.ImplementationRoadmap.State, null, null),
            ["executivePresentation"] = content => new(content.ExecutivePresentation.State, content.ExecutivePresentation.Narrative, content.ExecutivePresentation.EvidenceReferences)
        };

    public AssessmentReportContentValidationResult Validate(
        ConsultantReportContent content,
        AssessmentReportContentValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);
        var issues = new List<ReportContentValidationIssue>();

        if (!string.Equals(content.SchemaVersion, ConsultantReportContent.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            Add("unsupported-schema-version", ReportContentValidationSeverity.Error, "schemaVersion",
                $"Unsupported consultant report content schema version '{content.SchemaVersion}'.");
            return new(issues);
        }

        ValidateSectionStates();
        ValidateSectionConsistency();
        ValidateFindings(content.ExecutiveSummary.Findings, "executiveSummary.findings");
        ValidateFindings(content.IndustrialDataAndSystems.Findings, "industrialDataAndSystems.findings");
        ValidateFindings(content.AiOpportunitiesAndRoi.Findings, "aiOpportunitiesAndRoi.findings");
        ValidateSystems();
        ValidateOpportunities();
        ValidateTechnicalSections();
        ValidateRoadmap();
        ValidateExecutivePresentation();
        ValidateReadiness();

        return new(issues);

        void Add(string code, ReportContentValidationSeverity severity, string path, string message) =>
            issues.Add(new(code, severity, path, message));

        void Required(bool condition, string code, string path, string message)
        {
            if (!condition)
            {
                Add(code, ReportContentValidationSeverity.Error, path, message);
            }
        }

        void ValidateSectionConsistency()
        {
            Check(content.ExecutiveSummary.State,
                !string.IsNullOrWhiteSpace(content.ExecutiveSummary.Narrative) ||
                content.ExecutiveSummary.Findings?.Count > 0 || content.ExecutiveSummary.EvidenceReferences?.Count > 0 ||
                content.ExecutiveSummary.Readiness is not null,
                "executiveSummary");
            Check(content.IndustrialDataAndSystems.State,
                content.IndustrialDataAndSystems.Systems?.Count > 0 || content.IndustrialDataAndSystems.Findings?.Count > 0 ||
                content.IndustrialDataAndSystems.EvidenceReferences?.Count > 0,
                "industrialDataAndSystems");
            Check(content.AiOpportunitiesAndRoi.State,
                content.AiOpportunitiesAndRoi.Opportunities?.Count > 0 || content.AiOpportunitiesAndRoi.Findings?.Count > 0,
                "aiOpportunitiesAndRoi");
            Check(content.TechnicalGapsAndArchitecture.State,
                content.TechnicalGapsAndArchitecture.Gaps?.Count > 0 || content.TechnicalGapsAndArchitecture.Recommendations?.Count > 0,
                "technicalGapsAndArchitecture");
            Check(content.ImplementationRoadmap.State, content.ImplementationRoadmap.Items?.Count > 0, "implementationRoadmap");
            Check(content.ExecutivePresentation.State,
                !string.IsNullOrWhiteSpace(content.ExecutivePresentation.Title) ||
                !string.IsNullOrWhiteSpace(content.ExecutivePresentation.Narrative) ||
                content.ExecutivePresentation.KeyMessages?.Count > 0 || content.ExecutivePresentation.DecisionsRequested?.Count > 0 ||
                content.ExecutivePresentation.TopRisks?.Count > 0 || content.ExecutivePresentation.EvidenceReferences?.Count > 0,
                "executivePresentation");

            ValidateReferences(content.ExecutiveSummary.EvidenceReferences, "executiveSummary.evidenceReferences", context.Stage != AssessmentReportContentStage.Draft);

            void Check(ReportAssessmentState state, bool hasContent, string path)
            {
                if (state != ReportAssessmentState.Assessed && hasContent)
                {
                    Add("section-state-content-conflict", ReportContentValidationSeverity.Error, path,
                        "A section marked not assessed, not available, or not applicable must not contain assessed content.");
                }
                if (state == ReportAssessmentState.Assessed && !hasContent && context.Stage != AssessmentReportContentStage.Draft)
                {
                    Add("assessed-section-content-required", ReportContentValidationSeverity.Error, path,
                        "An assessed section must contain section content.");
                }
            }
        }

        void ValidateSectionStates()
        {
            foreach (var section in RequiredSections)
            {
                var state = section.Value(content);
                if (context.Stage != AssessmentReportContentStage.Draft && state.State == ReportAssessmentState.NotAssessed)
                {
                    Add("section-not-assessed", ReportContentValidationSeverity.Error, section.Key,
                        "This section must be assessed or explicitly marked not available/not applicable before review.");
                }
                if (state.State == ReportAssessmentState.NotAvailable &&
                    context.Stage is AssessmentReportContentStage.Approved or AssessmentReportContentStage.Issued)
                {
                    Add("section-not-available", ReportContentValidationSeverity.Error, section.Key,
                        "A section cannot be marked not available in an approved or issued report; document an assessment or an applicable not-assessed/not-applicable status.");
                }
            }
        }

        void ValidateFindings(IReadOnlyList<ReportFinding>? findings, string path)
        {
            var values = findings ?? Array.Empty<ReportFinding>();
            ValidateUniqueIds(values.Select(item => item.Id), path);
            for (var index = 0; index < values.Count; index++)
            {
                var item = values[index];
                var itemPath = $"{path}[{index}]";
                Required(!string.IsNullOrWhiteSpace(item.Title), "finding-title-required", itemPath + ".title", "Finding title is required.");
                Required(!string.IsNullOrWhiteSpace(item.Narrative), "finding-narrative-required", itemPath + ".narrative", "Finding narrative is required.");
                if (item.Classification == ReportFindingClassification.UnsupportedClaim)
                {
                    Add("unsupported-claim", context.Stage == AssessmentReportContentStage.Draft
                        ? ReportContentValidationSeverity.Warning : ReportContentValidationSeverity.Error,
                        itemPath, "Unsupported claims must be substantiated, reframed as a preliminary finding, or removed.");
                }
                if (item.Classification == ReportFindingClassification.Finding &&
                    item.VerificationStatus is ReportVerificationStatus.Unverified or ReportVerificationStatus.NotApplicable)
                {
                    Add("unverified-finding", context.Stage == AssessmentReportContentStage.Draft
                        ? ReportContentValidationSeverity.Warning : ReportContentValidationSeverity.Error,
                        itemPath, "A factual finding must be verified or explicitly classified as preliminary.");
                }
                ValidateReferences(item.EvidenceReferences, itemPath + ".evidenceReferences", requireEvidence: context.Stage != AssessmentReportContentStage.Draft);
            }
        }

        void ValidateSystems()
        {
            var systems = content.IndustrialDataAndSystems.Systems ?? Array.Empty<SystemAssessmentEntry>();
            ValidateUniqueIds(systems.Select(item => item.Id), "industrialDataAndSystems.systems");
            for (var index = 0; index < systems.Count; index++)
            {
                var item = systems[index];
                var path = $"industrialDataAndSystems.systems[{index}]";
                Required(!string.IsNullOrWhiteSpace(item.Name), "system-name-required", path + ".name", "System name is required.");
                if (context.Stage is AssessmentReportContentStage.Approved or AssessmentReportContentStage.Issued && item.AssessmentState == ReportAssessmentState.Assessed)
                {
                    Required(!string.IsNullOrWhiteSpace(item.SystemType), "system-type-required", path + ".systemType", "Assessed system type is required.");
                    Required(!string.IsNullOrWhiteSpace(item.OperationalArea), "system-area-required", path + ".operationalArea", "Assessed operational area is required.");
                    Required(!string.IsNullOrWhiteSpace(item.DataExchanged), "system-data-required", path + ".dataExchanged", "Assessed data exchanged is required.");
                }
                ValidateReferences(item.EvidenceReferences, path + ".evidenceReferences", context.Stage != AssessmentReportContentStage.Draft);
            }
        }

        void ValidateOpportunities()
        {
            var opportunities = content.AiOpportunitiesAndRoi.Opportunities ?? Array.Empty<AiImplementationOpportunity>();
            ValidateUniqueIds(opportunities.Select(item => item.Id), "aiOpportunitiesAndRoi.opportunities");
            for (var index = 0; index < opportunities.Count; index++)
            {
                var item = opportunities[index];
                var path = $"aiOpportunitiesAndRoi.opportunities[{index}]";
                if (context.Stage != AssessmentReportContentStage.Draft)
                {
                    Required(!string.IsNullOrWhiteSpace(item.BusinessProblem), "opportunity-problem-required", path + ".businessProblem", "Business problem is required.");
                    Required(!string.IsNullOrWhiteSpace(item.OperationalProcessOrArea), "opportunity-area-required", path + ".operationalProcessOrArea", "Operational area is required.");
                    Required(!string.IsNullOrWhiteSpace(item.ProposedUseCase), "opportunity-use-case-required", path + ".proposedUseCase", "Proposed use case is required.");
                    Required(!string.IsNullOrWhiteSpace(item.ExpectedMeasurableOutcome), "opportunity-outcome-required", path + ".expectedMeasurableOutcome", "Expected measurable outcome or explicit not-estimated state is required.");
                    Required(item.RequiredDataSources is not null, "opportunity-data-sources-required", path + ".requiredDataSources", "Required data sources must be explicitly listed, including an empty list when none are identified.");
                    Required(item.TechnicalPrerequisites is not null, "opportunity-prerequisites-required", path + ".technicalPrerequisites", "Technical prerequisites must be explicitly listed.");
                    Required(item.ImplementationDependencies is not null, "opportunity-dependencies-required", path + ".implementationDependencies", "Implementation dependencies must be explicitly listed.");
                    Required(item.RisksAndAssumptions is not null, "opportunity-risks-required", path + ".risksAndAssumptions", "Risks and assumptions must be explicitly listed.");
                }
                ValidateEstimate(item.Estimate, path + ".estimate");
                ValidateReferences(item.EvidenceReferences, path + ".evidenceReferences", context.Stage != AssessmentReportContentStage.Draft);
                if (item.VerificationStatus == ReportVerificationStatus.Unverified && context.Stage != AssessmentReportContentStage.Draft)
                {
                    Add("opportunity-unverified", ReportContentValidationSeverity.Error, path,
                        "An unverified implementation opportunity cannot be approved or issued.");
                }
            }
        }

        void ValidateEstimate(EffortCostEstimate estimate, string path)
        {
            if (estimate.Status == ReportEstimateStatus.NotEstimated)
            {
                Required(estimate.EffortValue is null && estimate.CostAmount is null,
                    "not-estimated-has-values", path, "A NotEstimated estimate must not contain numeric effort or cost values.");
                return;
            }

            Required(estimate.EffortValue is > 0 || estimate.CostAmount is > 0,
                "estimate-value-required", path, "An estimate must include a positive effort or cost value.");
            Required(estimate.CostAmount is null || !string.IsNullOrWhiteSpace(estimate.Currency),
                "estimate-currency-required", path + ".currency", "Currency is required when cost is estimated.");
            Required(estimate.EffortValue is null || !string.IsNullOrWhiteSpace(estimate.EffortUnit),
                "estimate-effort-unit-required", path + ".effortUnit", "Effort unit is required when effort is estimated.");
            if ((estimate.CostAmount.HasValue || estimate.EffortValue.HasValue) &&
                (estimate.EvidenceReferences is null || estimate.EvidenceReferences.Count == 0) &&
                (estimate.Assumptions is null || estimate.Assumptions.Count == 0 || string.IsNullOrWhiteSpace(estimate.Basis)))
            {
                Add("estimate-basis-required", context.Stage == AssessmentReportContentStage.Draft
                    ? ReportContentValidationSeverity.Warning : ReportContentValidationSeverity.Error,
                    path, "Quantified estimates require evidence, or an explicit basis and assumptions; otherwise use NotEstimated.");
            }
            ValidateReferences(estimate.EvidenceReferences, path + ".evidenceReferences", context.Stage != AssessmentReportContentStage.Draft);
        }

        void ValidateTechnicalSections()
        {
            var gaps = content.TechnicalGapsAndArchitecture.Gaps ?? Array.Empty<TechnicalGapEntry>();
            var recommendations = content.TechnicalGapsAndArchitecture.Recommendations ?? Array.Empty<ArchitectureRecommendation>();
            ValidateUniqueIds(gaps.Select(item => item.Id), "technicalGapsAndArchitecture.gaps");
            ValidateUniqueIds(recommendations.Select(item => item.Id), "technicalGapsAndArchitecture.recommendations");
            for (var index = 0; index < gaps.Count; index++)
            {
                var item = gaps[index];
                if (context.Stage is AssessmentReportContentStage.Approved or AssessmentReportContentStage.Issued)
                {
                    Required(!string.IsNullOrWhiteSpace(item.Title), "technical-gap-title-required", $"technicalGapsAndArchitecture.gaps[{index}].title", "Technical gap title is required.");
                    Required(!string.IsNullOrWhiteSpace(item.Description), "technical-gap-description-required", $"technicalGapsAndArchitecture.gaps[{index}].description", "Technical gap description is required.");
                    Required(!string.IsNullOrWhiteSpace(item.Impact), "technical-gap-impact-required", $"technicalGapsAndArchitecture.gaps[{index}].impact", "Technical gap impact is required.");
                }
                ValidateReferences(item.EvidenceReferences, $"technicalGapsAndArchitecture.gaps[{index}].evidenceReferences", context.Stage != AssessmentReportContentStage.Draft);
                ValidateVerification(item.VerificationStatus, item.RequiresValidation, $"technicalGapsAndArchitecture.gaps[{index}]");
            }
            for (var index = 0; index < recommendations.Count; index++)
            {
                var item = recommendations[index];
                if (context.Stage is AssessmentReportContentStage.Approved or AssessmentReportContentStage.Issued)
                {
                    Required(!string.IsNullOrWhiteSpace(item.Title), "recommendation-title-required", $"technicalGapsAndArchitecture.recommendations[{index}].title", "Recommendation title is required.");
                    Required(!string.IsNullOrWhiteSpace(item.Recommendation), "recommendation-text-required", $"technicalGapsAndArchitecture.recommendations[{index}].recommendation", "Recommendation text is required.");
                    Required(!string.IsNullOrWhiteSpace(item.Rationale), "recommendation-rationale-required", $"technicalGapsAndArchitecture.recommendations[{index}].rationale", "Recommendation rationale is required.");
                }
                ValidateReferences(item.EvidenceReferences, $"technicalGapsAndArchitecture.recommendations[{index}].evidenceReferences", context.Stage != AssessmentReportContentStage.Draft);
                ValidateVerification(item.VerificationStatus, item.RequiresValidation, $"technicalGapsAndArchitecture.recommendations[{index}]");
            }
        }

        void ValidateVerification(ReportVerificationStatus verification, bool requiresValidation, string path)
        {
            if (context.Stage is AssessmentReportContentStage.Approved or AssessmentReportContentStage.Issued &&
                (verification == ReportVerificationStatus.Unverified || requiresValidation))
            {
                Add("validation-required", ReportContentValidationSeverity.Error, path,
                    "Approved or issued content cannot contain unverified or explicitly unvalidated recommendations/gaps.");
            }
        }

        void ValidateRoadmap()
        {
            var items = content.ImplementationRoadmap.Items ?? Array.Empty<RoadmapItem>();
            ValidateUniqueIds(items.Select(item => item.Id), "implementationRoadmap.items");
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var path = $"implementationRoadmap.items[{index}]";
                if (context.Stage is AssessmentReportContentStage.Approved or AssessmentReportContentStage.Issued)
                {
                    Required(!string.IsNullOrWhiteSpace(item.Title), "roadmap-title-required", path + ".title", "Roadmap title is required.");
                    Required(!string.IsNullOrWhiteSpace(item.Activities), "roadmap-activities-required", path + ".activities", "Roadmap activities are required.");
                    Required(item.AcceptanceCriteria is not null && item.AcceptanceCriteria.Count > 0, "roadmap-acceptance-required", path + ".acceptanceCriteria", "At least one acceptance criterion is required.");
                }
                Required(item.StartDay >= 1 && item.EndDay <= 90 && item.StartDay <= item.EndDay,
                    "roadmap-boundary", path, "Roadmap items must have valid inclusive day bounds within days 1-90.");
                Required(item.Priority > 0, "roadmap-priority", path + ".priority", "Priority must be a positive ordinal.");
                ValidateReferences(item.EvidenceReferences, path + ".evidenceReferences", context.Stage != AssessmentReportContentStage.Draft);
            }
            var duplicates = items.GroupBy(item => item.Priority).Where(group => group.Count() > 1);
            foreach (var duplicate in duplicates)
            {
                Add("roadmap-priority-duplicate", ReportContentValidationSeverity.Error, "implementationRoadmap.items",
                    $"Roadmap priority {duplicate.Key} is assigned more than once.");
            }
            var ids = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in items)
            {
                foreach (var dependency in item.Dependencies)
                {
                    Required(ids.Contains(dependency), "roadmap-dependency-missing", $"implementationRoadmap.items[{item.Id}].dependencies",
                        $"Roadmap dependency '{dependency}' does not identify an item in the roadmap.");
                    if (ids.Contains(dependency))
                    {
                        var dependencyItem = items.Single(candidate => candidate.Id == dependency);
                        Required(dependencyItem.Id != item.Id, "roadmap-dependency-cycle", $"implementationRoadmap.items[{item.Id}].dependencies", "A roadmap item cannot depend on itself.");
                        Required(dependencyItem.EndDay <= item.StartDay, "roadmap-dependency-order",
                            $"implementationRoadmap.items[{item.Id}].dependencies", "Dependencies must finish no later than the dependent item's start day.");
                    }
                }
            }
        }

        void ValidateExecutivePresentation()
        {
            var section = content.ExecutivePresentation;
            var entries = (section.KeyMessages ?? Array.Empty<ReportTextEntry>())
                .Concat(section.DecisionsRequested ?? Array.Empty<ReportTextEntry>()).ToArray();
            ValidateUniqueIds(entries.Select(item => item.Id), "executivePresentation");
            for (var index = 0; index < entries.Length; index++)
            {
                ValidateReferences(entries[index].EvidenceReferences,
                    $"executivePresentation.entries[{index}].evidenceReferences", context.Stage != AssessmentReportContentStage.Draft);
            }
            ValidateReferences(section.EvidenceReferences, "executivePresentation.evidenceReferences", context.Stage != AssessmentReportContentStage.Draft);
        }

        void ValidateReadiness()
        {
            var represented = content.ExecutiveSummary.Readiness;
            var snapshot = context.ScoringSnapshot;
            if (represented is not null && snapshot is null && context.Stage != AssessmentReportContentStage.Draft)
            {
                Add("scoring-snapshot-required", ReportContentValidationSeverity.Error, "executiveSummary.readiness",
                    "Readiness score representations require the authoritative scoring snapshot for validation.");
                return;
            }
            if (represented is null || snapshot is null)
            {
                return;
            }

            if (represented.OverallStatus != snapshot.OverallStatus ||
                represented.OverallRawScore0To100 != snapshot.OverallRawScore0To100 ||
                represented.OverallDisplayScore0To100 != snapshot.OverallDisplayScore0To100 ||
                represented.OverallClassification != snapshot.OverallClassification)
            {
                Add("readiness-score-conflict", ReportContentValidationSeverity.Error, "executiveSummary.readiness",
                    "Content readiness status, scores, or classification conflict with the authoritative scoring snapshot.");
            }

            if (snapshot.OverallStatus != ReadinessOverallStatus.Eligible &&
                (represented.OverallRawScore0To100 is not null || represented.OverallDisplayScore0To100 is not null || represented.OverallClassification is not null))
            {
                Add("score-present-for-noneligible-status", ReportContentValidationSeverity.Error, "executiveSummary.readiness",
                    "Non-eligible scoring outcomes must preserve null score and classification values.");
            }

            var dimensionRepresentations = represented.Dimensions.ToDictionary(item => item.DimensionId, StringComparer.Ordinal);
            if (dimensionRepresentations.Count != snapshot.Dimensions.Count ||
                snapshot.Dimensions.Any(dimension => !dimensionRepresentations.TryGetValue(dimension.DimensionId, out var item) ||
                    item.Status != dimension.Status || item.RawScore0To100 != dimension.RawScore0To100 ||
                    item.DisplayScore0To100 != dimension.DisplayScore0To100 || item.Classification != dimension.Classification))
            {
                Add("dimension-score-conflict", ReportContentValidationSeverity.Error, "executiveSummary.readiness.dimensions",
                    "Dimension statuses and nullable score values must match the authoritative scoring snapshot exactly.");
            }
        }

        void ValidateUniqueIds(IEnumerable<string> identifiers, string path)
        {
            var ids = identifiers.ToArray();
            if (ids.Any(string.IsNullOrWhiteSpace))
            {
                Add("stable-id-required", ReportContentValidationSeverity.Error, path, "Every record must have a non-empty stable identifier.");
            }
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            {
                Add("stable-id-duplicate", ReportContentValidationSeverity.Error, path, "Stable identifiers must be unique within their collection.");
            }
        }

        void ValidateReferences(IReadOnlyList<ReportEvidenceReference>? references, string path, bool requireEvidence)
        {
            var evidence = references ?? Array.Empty<ReportEvidenceReference>();
            if (requireEvidence && evidence.Count == 0)
            {
                Add("evidence-required", ReportContentValidationSeverity.Error, path, "At least one valid evidence reference is required.");
            }
            for (var index = 0; index < evidence.Count; index++)
            {
                var reference = evidence[index];
                var itemPath = $"{path}[{index}]";
                if (string.IsNullOrWhiteSpace(reference.ReferenceId))
                {
                    Add("evidence-id-required", ReportContentValidationSeverity.Error, itemPath, "Evidence reference ID is required.");
                    continue;
                }
                var valid = reference.Kind switch
                {
                    ReportEvidenceKind.QuestionnaireAnswer => IsValidQuestionnaireReference(reference),
                    ReportEvidenceKind.ScoringItem => IsValidScoringItemReference(reference),
                    ReportEvidenceKind.ScoringDimension => context.ScoringSnapshot?.Dimensions.Any(dimension => dimension.DimensionId == reference.ReferenceId) == true,
                    ReportEvidenceKind.ScoringReviewFlag => context.ScoringSnapshot?.ReviewFlags.Any(flag => flag.Code == reference.ReferenceId) == true,
                    ReportEvidenceKind.ConsultantObservation => context.ConsultantObservationIds?.Contains(reference.ReferenceId) == true,
                    _ => false
                };
                if (!valid)
                {
                    Add("evidence-reference-invalid", ReportContentValidationSeverity.Error, itemPath,
                        $"Evidence reference '{reference.ReferenceId}' does not identify a source in the supplied assessment context.");
                }
            }
        }

        bool IsValidQuestionnaireReference(ReportEvidenceReference reference)
        {
            var question = context.QuestionnaireDefinition?.Sections.SelectMany(section => section.Questions)
                .FirstOrDefault(item => item.Id == reference.QuestionId);
            if (question is null || !string.Equals(reference.ReferenceId, reference.QuestionId, StringComparison.Ordinal))
            {
                return false;
            }
            if (context.QuestionnaireResponse is null || !context.QuestionnaireResponse.Answers.ContainsKey(question.Id))
            {
                return false;
            }
            if (reference.FieldId is not null)
            {
                return question.RepeatableFields?.Any(field => field.Id == reference.FieldId) == true ||
                    question.Type == SentinelMfgSuite.Web.Models.Assessments.Questionnaire.QuestionType.Composite &&
                    question.Id == reference.FieldId;
            }
            return true;
        }

        bool IsValidScoringItemReference(ReportEvidenceReference reference) =>
            context.ScoringSnapshot?.Dimensions.SelectMany(dimension => dimension.Items).Any(item =>
                string.Equals(item.QuestionId, reference.QuestionId, StringComparison.Ordinal) &&
                string.Equals(item.FieldId, reference.FieldId, StringComparison.Ordinal) &&
                string.Equals(item.ItemKey, reference.ItemKey, StringComparison.Ordinal) &&
                string.Equals(reference.ReferenceId, item.QuestionId + ":" + item.ItemKey, StringComparison.Ordinal)) == true;
    }

    private sealed record ReportSectionState(
        ReportAssessmentState State,
        string? Narrative,
        IReadOnlyList<ReportEvidenceReference>? EvidenceReferences);
}
