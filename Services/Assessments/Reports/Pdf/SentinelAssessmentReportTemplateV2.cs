using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;

/// <summary>
/// Template v2 of the Sentinel AI Readiness Assessment report. Reads only the immutable
/// <see cref="ReportPdfRenderModel"/>; it has no access to storage, HTTP, the database, live scoring,
/// questionnaire definitions or submitted responses.
/// </summary>
public sealed class SentinelAssessmentReportTemplateV2 : IReportPdfTemplate
{
    private const string FontFamily = "Lato";
    private const string Navy = "#12263F";
    private const string Accent = "#1F5F8B";
    private const string Muted = "#5A6675";
    private const string Rule = "#C9D1DA";
    private const string Band = "#EEF2F6";
    private const string Unavailable = "No entries have been recorded for this section in this revision.";

    public string Version => ReportTemplateVersions.V2;

    public byte[] Render(ReportPdfRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        ReportPdfRuntime.EnsureInitialized();

        var issued = new DateTimeOffset(DateTime.SpecifyKind(model.IssuedUtc, DateTimeKind.Utc));
        var title = string.IsNullOrWhiteSpace(model.AssessmentTitle) ? "AI Readiness Assessment" : model.AssessmentTitle!;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(60);
                page.DefaultTextStyle(x => x.FontFamily(FontFamily).FontSize(10).FontColor(Navy));
                page.Content().Element(c => ComposeCover(c, model, title));
            });

            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(50);
                page.MarginVertical(45);
                page.DefaultTextStyle(x => x.FontFamily(FontFamily).FontSize(10).FontColor(Navy).LineHeight(1.3f));
                page.Header().Element(c => ComposeHeader(c, title));
                page.Content().PaddingVertical(10).Element(c => ComposeBody(c, model));
                page.Footer().Element(c => ComposeFooter(c, model));
            });
        });

        document.WithMetadata(new DocumentMetadata
        {
            Title = $"{title} - Revision {model.RevisionNumber}",
            Author = "Sentinel AI Systems",
            Subject = "AI Readiness Assessment Report",
            Creator = "Sentinel AI Systems",
            Producer = "Sentinel AI Systems",
            Keywords = $"{model.TemplateVersion}; revision {model.RevisionNumber}",
            Language = "en-US",
            CreationDate = issued,
            ModifiedDate = issued
        });

        return document.GeneratePdf();
    }

    // ---------------------------------------------------------------- cover

    private static void ComposeCover(IContainer container, ReportPdfRenderModel model, string title)
    {
        container.Column(column =>
        {
            column.Item().Text("SENTINEL AI SYSTEMS").FontSize(14).Bold().FontColor(Accent).LetterSpacing(0.15f);
            column.Item().PaddingTop(6).LineHorizontal(2).LineColor(Accent);

            column.Item().PaddingTop(150).Text("AI Readiness Assessment").FontSize(34).Bold();
            column.Item().PaddingTop(8).Text(title).FontSize(20).FontColor(Accent);

            column.Item().PaddingTop(40).Column(details =>
            {
                details.Spacing(6);
                details.Item().Text(text => LabelValue(text, "Site", Text(model.SiteName, "Not recorded")));
                details.Item().Text(text => LabelValue(text, "Revision", model.RevisionNumber.ToString(CultureInfo.InvariantCulture)));
                details.Item().Text(text => LabelValue(text, "Issued", FormatDate(model.IssuedUtc)));
                if (model.BasedOnRevisionId is not null)
                {
                    details.Item().Text(text => LabelValue(text, "Report type", "Corrected revision"));
                }
            });

            column.Item().ExtendVertical().AlignBottom().Text(
                "Confidential. Prepared by Sentinel AI Systems for the named site.").FontSize(9).FontColor(Muted);
        });
    }

    private static void ComposeHeader(IContainer container, string title)
    {
        container.PaddingBottom(8).BorderBottom(0.75f).BorderColor(Rule).Row(row =>
        {
            row.RelativeItem().Text("Sentinel AI Systems").Bold().FontColor(Accent);
            row.RelativeItem().AlignRight().Text(title).FontColor(Muted).FontSize(9);
        });
    }

    private static void ComposeFooter(IContainer container, ReportPdfRenderModel model)
    {
        container.PaddingTop(6).BorderTop(0.75f).BorderColor(Rule).Row(row =>
        {
            row.RelativeItem().Text(
                $"Revision {model.RevisionNumber} - issued {FormatDate(model.IssuedUtc)}").FontSize(8).FontColor(Muted);
            row.RelativeItem().AlignRight().Text(text =>
            {
                text.DefaultTextStyle(x => x.FontSize(8).FontColor(Muted));
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });
    }

    // ----------------------------------------------------------------- body

    private static void ComposeBody(IContainer container, ReportPdfRenderModel model)
    {
        var content = model.Content;
        var snapshot = model.Snapshot;

        container.Column(column =>
        {
            column.Spacing(14);

            column.Item().Element(c => Section(c, "1. Executive Summary", body => ExecutiveSummary(body, model)));
            column.Item().Element(c => Section(c, "2. Scope and Methodology", body => ScopeAndMethodology(body, model)));
            column.Item().Element(c => Section(c, "3. Overall AI Readiness", body => OverallReadiness(body, snapshot)));
            column.Item().Element(c => Section(c, "4. Readiness Dimensions and Scores", body => Dimensions(body, snapshot)));
            column.Item().Element(c => Section(c, "5. Key Findings and Evidence", body => Findings(body, model)));
            column.Item().Element(c => Section(c, "6. Operational and Technical Gaps", body => Gaps(body, model)));
            column.Item().Element(c => Section(c, "7. Prioritized Recommendations", body => Recommendations(body, model)));
            column.Item().Element(c => Section(c, "8. Implementation Roadmap", body => Roadmap(body, content)));
            column.Item().Element(c => Section(c, "9. Assumptions and Limitations", body => Limitations(body, model)));
            column.Item().Element(c => Section(c, "10. Next Steps", body => NextSteps(body, content)));
            column.Item().Element(c => Section(c, "11. Report Version and Issuance", body => Issuance(body, model)));
        });
    }

    private static void Section(IContainer container, string heading, Action<IContainer> body)
    {
        container.EnsureSpace(140).Column(column =>
        {
            column.Item().ShowEntire().Column(head =>
            {
                head.Item().Text(heading).FontSize(15).Bold().FontColor(Accent);
                head.Item().PaddingTop(3).LineHorizontal(1).LineColor(Rule);
            });
            column.Item().PaddingTop(8).Element(body);
        });
    }

    // ------------------------------------------------------------ sections

    private static void ExecutiveSummary(IContainer container, ReportPdfRenderModel model)
    {
        var summary = model.Content.ExecutiveSummary;
        var presentation = model.Content.ExecutivePresentation;

        container.Column(column =>
        {
            column.Spacing(6);
            StateLine(column, summary.State);
            if (!string.IsNullOrWhiteSpace(summary.Narrative))
            {
                column.Item().Text(summary.Narrative);
            }
            else
            {
                column.Item().Text(Unavailable).Italic().FontColor(Muted);
            }

            if (presentation.KeyMessages is { Count: > 0 })
            {
                column.Item().PaddingTop(4).Text("Key messages").Bold();
                foreach (var message in presentation.KeyMessages)
                {
                    column.Item().Text($"- {message.Text}");
                }
            }

            if (presentation.TopRisks is { Count: > 0 })
            {
                column.Item().PaddingTop(4).Text("Top risks").Bold();
                foreach (var risk in presentation.TopRisks)
                {
                    column.Item().Text($"- {risk}");
                }
            }
        });
    }

    private static void ScopeAndMethodology(IContainer container, ReportPdfRenderModel model)
    {
        var content = model.Content;
        container.Column(column =>
        {
            column.Spacing(6);
            column.Item().Text("This section records only the factual parameters of this report revision.").FontColor(Muted);
            column.Item().Element(c => KeyValueTable(c, new (string, string)[]
            {
                ("Assessment", Text(model.AssessmentTitle, "Not recorded")),
                ("Site", Text(model.SiteName, "Not recorded")),
                ("Questionnaire version", model.QuestionnaireVersion),
                ("Questionnaire response submitted", FormatDate(model.ResponseSubmittedUtc)),
                ("Evidence provenance", Humanize(model.Snapshot.EvidenceProvenance.ToString()))
            }));

            column.Item().PaddingTop(4).Text("Consultant content coverage").Bold();
            column.Item().Element(c => KeyValueTable(c, new (string, string)[]
            {
                ("Executive summary", Humanize(content.ExecutiveSummary.State.ToString())),
                ("Industrial data and systems", Humanize(content.IndustrialDataAndSystems.State.ToString())),
                ("AI opportunities and ROI", Humanize(content.AiOpportunitiesAndRoi.State.ToString())),
                ("Technical gaps and architecture", Humanize(content.TechnicalGapsAndArchitecture.State.ToString())),
                ("Implementation roadmap", Humanize(content.ImplementationRoadmap.State.ToString())),
                ("Executive presentation", Humanize(content.ExecutivePresentation.State.ToString()))
            }));

            column.Item().PaddingTop(4).Text("Scored dimensions").Bold();
            if (model.Snapshot.Dimensions.Count == 0)
            {
                column.Item().Text("No scored dimensions are recorded for this revision.").Italic().FontColor(Muted);
            }

            foreach (var dimension in model.Snapshot.Dimensions)
            {
                column.Item().Text(
                    $"- {dimension.Name} (weight {dimension.Weight.ToString("0.####", CultureInfo.InvariantCulture)}; " +
                    $"{dimension.ScoreableItemCount} of {dimension.TotalEligibleItemCount} items scoreable)");
            }
        });
    }

    private static void OverallReadiness(IContainer container, ReadinessScoringSnapshot snapshot)
    {
        container.Column(column =>
        {
            column.Spacing(6);
            var hasScore = snapshot.OverallStatus == ReadinessOverallStatus.Eligible
                && snapshot.OverallDisplayScore0To100 is not null;

            column.Item().Element(c => KeyValueTable(c, new (string, string)[]
            {
                ("Status", Humanize(snapshot.OverallStatus.ToString())),
                ("Overall score", hasScore ? $"{snapshot.OverallDisplayScore0To100} / 100" : "Not scored (withheld)"),
                ("Classification", hasScore && snapshot.OverallClassification is { } cls ? cls.ToString() : "Not classified")
            }));

            if (hasScore)
            {
                column.Item().Text("Classification bands: Foundational 0-24, Developing 25-49, Established 50-74, Advanced 75-100.").FontSize(9).FontColor(Muted);
            }
            else
            {
                column.Item().Text("No overall numeric score is reported for this revision.").Italic().FontColor(Muted);
            }

            if (snapshot.WithholdingReasons.Count > 0)
            {
                column.Item().Text("Reasons recorded for withholding the overall score").Bold();
                foreach (var reason in snapshot.WithholdingReasons)
                {
                    column.Item().Text($"- {Humanize(reason.ToString())}");
                }
            }
        });
    }

    private static void Dimensions(IContainer container, ReadinessScoringSnapshot snapshot)
    {
        if (snapshot.Dimensions.Count == 0)
        {
            container.Text("No dimension scores are recorded for this revision.").Italic().FontColor(Muted);
            return;
        }

        container.Column(outer =>
        {
        outer.Spacing(6);
        outer.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(3.2f);
                columns.RelativeColumn(1f);
                columns.RelativeColumn(1.4f);
                columns.RelativeColumn(1.3f);
                columns.RelativeColumn(1.6f);
                columns.RelativeColumn(1.4f);
            });

            table.Header(header =>
            {
                foreach (var title in new[] { "Dimension", "Weight", "Status", "Score", "Classification", "Items scored" })
                {
                    header.Cell().Element(HeaderCell).Text(title).Bold().FontColor(Colors.White);
                }
            });

            foreach (var dimension in snapshot.Dimensions)
            {
                var scored = dimension.Status == ReadinessDimensionStatus.Eligible && dimension.DisplayScore0To100 is not null;
                table.Cell().Element(BodyCell).Text(dimension.Name);
                table.Cell().Element(BodyCell).Text(dimension.Weight.ToString("0.####", CultureInfo.InvariantCulture));
                table.Cell().Element(BodyCell).Text(Humanize(dimension.Status.ToString()));
                table.Cell().Element(BodyCell).Text(scored ? $"{dimension.DisplayScore0To100} / 100" : "Withheld");
                table.Cell().Element(BodyCell).Text(scored && dimension.Classification is { } cls ? cls.ToString() : "Not classified");
                table.Cell().Element(BodyCell).Text($"{dimension.ScoreableItemCount} of {dimension.TotalEligibleItemCount}");
            }
        });

        outer.Item().Text("How to read these scores").Bold();
        foreach (var dimension in snapshot.Dimensions)
        {
            var description = ReportEvidenceCatalog.FindDimension(snapshot.QuestionnaireVersion, dimension.DimensionId);
            if (description is null) continue;
            outer.Item().Text(text =>
            {
                text.Span($"{dimension.Name}: ").Bold();
                text.Span($"reflects {description.WhatItMeasures} It does not reflect {description.DoesNotMeasure}");
            });
        }

        outer.Item().Text("The overall score is the weighted average of the dimension scores, using the weights shown above. A dimension is scored only when enough of its questions have a usable answer; otherwise it is withheld and no overall score is given. Scores summarize the questionnaire answers submitted by the customer and have not been verified on site. They are not proof of operational performance, safety, return on investment or suitability for AI, and a strong score in one dimension does not offset a weak score in another. Consultant findings may describe operational limitations that a single dimension score does not capture.").FontSize(9).FontColor(Muted);
        });
    }

    private static void Findings(IContainer container, ReportPdfRenderModel model)
    {
        var content = model.Content;
        var groups = new (string Name, IReadOnlyList<ReportFinding>? Findings)[]
        {
            ("Executive summary findings", content.ExecutiveSummary.Findings),
            ("Industrial data and systems findings", content.IndustrialDataAndSystems.Findings),
            ("AI opportunity findings", content.AiOpportunitiesAndRoi.Findings)
        };

        container.Column(column =>
        {
            column.Spacing(8);
            if (groups.All(group => group.Findings is null || group.Findings.Count == 0))
            {
                column.Item().Text("No findings are recorded in this revision.").Italic().FontColor(Muted);
            }

            foreach (var (name, findings) in groups)
            {
                if (findings is null || findings.Count == 0)
                {
                    continue;
                }

                column.Item().Text(name).Bold().FontColor(Accent);
                foreach (var finding in findings)
                {
                    column.Item().ShowEntire().Border(0.75f).BorderColor(Rule).Padding(8).Column(card =>
                    {
                        card.Spacing(3);
                        card.Item().Text(finding.Title).Bold();
                        card.Item().Text(
                            $"{Humanize(finding.Classification.ToString())} | {Humanize(finding.VerificationStatus.ToString())} | " +
                            $"{Humanize(finding.Provenance.ToString())}").FontSize(8.5f).FontColor(Muted);
                        card.Item().Text(finding.Narrative);
                        EvidenceBlock(card, finding.EvidenceReferences, model.Snapshot);
                    });
                }
            }
        });
    }

    private static void Gaps(IContainer container, ReportPdfRenderModel model)
    {
        var systems = model.Content.IndustrialDataAndSystems;
        var gaps = model.Content.TechnicalGapsAndArchitecture;

        container.Column(column =>
        {
            column.Spacing(8);
            column.Item().Text("Industrial data and systems").Bold().FontColor(Accent);
            StateLine(column, systems.State);
            if (systems.Systems is { Count: > 0 })
            {
                foreach (var system in systems.Systems)
                {
                    column.Item().ShowEntire().Border(0.75f).BorderColor(Rule).Padding(8).Column(card =>
                    {
                        card.Spacing(3);
                        card.Item().Text(system.Name).Bold();
                        card.Item().Text(
                            $"{Humanize(system.AssessmentState.ToString())} | {Humanize(system.VerificationStatus.ToString())}")
                            .FontSize(8.5f).FontColor(Muted);
                        OptionalLine(card, "Type", system.SystemType);
                        OptionalLine(card, "Owner", system.Owner);
                        OptionalLine(card, "Operational area", system.OperationalArea);
                        OptionalLine(card, "Data exchanged", system.DataExchanged);
                        OptionalLine(card, "Integration pattern", system.IntegrationPattern);
                        OptionalLine(card, "Criticality", system.Criticality);
                        EvidenceBlock(card, system.EvidenceReferences, model.Snapshot);
                    });
                }
            }
            else
            {
                column.Item().Text(Unavailable).Italic().FontColor(Muted);
            }

            column.Item().PaddingTop(6).Text("Technical gaps").Bold().FontColor(Accent);
            StateLine(column, gaps.State);
            if (gaps.Gaps is { Count: > 0 })
            {
                foreach (var gap in gaps.Gaps)
                {
                    column.Item().ShowEntire().Border(0.75f).BorderColor(Rule).Padding(8).Column(card =>
                    {
                        card.Spacing(3);
                        card.Item().Text(gap.Title).Bold();
                        card.Item().Text(
                            $"{Humanize(gap.VerificationStatus.ToString())}{(gap.RequiresValidation ? " | Requires validation" : string.Empty)}")
                            .FontSize(8.5f).FontColor(Muted);
                        card.Item().Text(gap.Description);
                        OptionalLine(card, "Impact", gap.Impact);
                        EvidenceBlock(card, gap.EvidenceReferences, model.Snapshot);
                    });
                }
            }
            else
            {
                column.Item().Text(Unavailable).Italic().FontColor(Muted);
            }
        });
    }

    private static void Recommendations(IContainer container, ReportPdfRenderModel model)
    {
        var architecture = model.Content.TechnicalGapsAndArchitecture;
        var opportunities = model.Content.AiOpportunitiesAndRoi;

        container.Column(column =>
        {
            column.Spacing(8);
            column.Item().Text("Architecture recommendations").Bold().FontColor(Accent);
            if (architecture.Recommendations is { Count: > 0 })
            {
                foreach (var recommendation in architecture.Recommendations)
                {
                    column.Item().ShowEntire().Border(0.75f).BorderColor(Rule).Padding(8).Column(card =>
                    {
                        card.Spacing(3);
                        card.Item().Text(recommendation.Title).Bold();
                        card.Item().Text(
                            $"{Humanize(recommendation.VerificationStatus.ToString())}{(recommendation.RequiresValidation ? " | Requires validation" : string.Empty)}")
                            .FontSize(8.5f).FontColor(Muted);
                        card.Item().Text(recommendation.Recommendation);
                        OptionalLine(card, "Rationale", recommendation.Rationale);
                        EvidenceBlock(card, recommendation.EvidenceReferences, model.Snapshot);
                    });
                }
            }
            else
            {
                column.Item().Text(Unavailable).Italic().FontColor(Muted);
            }

            column.Item().PaddingTop(6).Text("AI implementation opportunities").Bold().FontColor(Accent);
            StateLine(column, opportunities.State);
            if (opportunities.Opportunities is { Count: > 0 })
            {
                foreach (var opportunity in opportunities.Opportunities)
                {
                    column.Item().ShowEntire().Border(0.75f).BorderColor(Rule).Padding(8).Column(card =>
                    {
                        card.Spacing(3);
                        card.Item().Text(opportunity.ProposedUseCase).Bold();
                        card.Item().Text(Humanize(opportunity.VerificationStatus.ToString())).FontSize(8.5f).FontColor(Muted);
                        OptionalLine(card, "Business problem", opportunity.BusinessProblem);
                        OptionalLine(card, "Operational area", opportunity.OperationalProcessOrArea);
                        OptionalLine(card, "Expected outcome", opportunity.ExpectedMeasurableOutcome);
                        ListLine(card, "Required data sources", opportunity.RequiredDataSources);
                        ListLine(card, "Technical prerequisites", opportunity.TechnicalPrerequisites);
                        ListLine(card, "Dependencies", opportunity.ImplementationDependencies);
                        ListLine(card, "Risks and assumptions", opportunity.RisksAndAssumptions);
                        card.Item().Text(text => LabelValue(text, "Estimate", DescribeEstimate(opportunity.Estimate)));
                        card.Item().Text(text => LabelValue(text, "Scope", DescribeScope(opportunity.Scope)));
                        EvidenceBlock(card, opportunity.EvidenceReferences, model.Snapshot);
                    });
                }
            }
            else
            {
                column.Item().Text(Unavailable).Italic().FontColor(Muted);
            }
        });
    }

    private static void Roadmap(IContainer container, ConsultantReportContent content)
    {
        var roadmap = content.ImplementationRoadmap;
        container.Column(column =>
        {
            column.Spacing(6);
            StateLine(column, roadmap.State);
            if (roadmap.Items is not { Count: > 0 })
            {
                column.Item().Text(Unavailable).Italic().FontColor(Muted);
                return;
            }

            foreach (var item in OrderedRoadmap(roadmap))
            {
                column.Item().ShowEntire().Border(0.75f).BorderColor(Rule).Padding(8).Column(card =>
                {
                    card.Spacing(3);
                    card.Item().Text($"Priority {item.Priority}: {item.Title}").Bold();
                    card.Item().Text($"Days {item.StartDay} to {item.EndDay} | {Humanize(item.VerificationStatus.ToString())}")
                        .FontSize(8.5f).FontColor(Muted);
                    card.Item().Text(item.Activities);
                    OptionalLine(card, "Owner role", item.OwnerRole);
                    ListLine(card, "Dependencies", item.Dependencies);
                    ListLine(card, "Acceptance criteria", item.AcceptanceCriteria);
                    ListLine(card, "Assumptions", item.Assumptions);
                    OptionalLine(card, "Success measure", item.SuccessMeasure);
                });
            }
        });
    }

    private static void Limitations(IContainer container, ReportPdfRenderModel model)
    {
        var snapshot = model.Snapshot;
        var content = model.Content;
        var lines = new List<string>();

        foreach (var reason in snapshot.WithholdingReasons)
        {
            lines.Add($"Overall score withheld: {Humanize(reason.ToString())}.");
        }

        foreach (var dimension in snapshot.Dimensions.Where(item => item.Status == ReadinessDimensionStatus.Withheld))
        {
            var reasons = dimension.WithholdingReasons.Count > 0
                ? string.Join(", ", dimension.WithholdingReasons.Select(reason => Humanize(reason.ToString())))
                : "no reason recorded";
            lines.Add($"{dimension.Name} score withheld: {reasons}.");
        }

        foreach (var dimension in snapshot.Dimensions)
        {
            var unscored = dimension.Items.Count(item => item.Status != ReadinessItemStatus.Scoreable);
            if (unscored > 0)
            {
                lines.Add($"{dimension.Name}: {unscored} of {dimension.Items.Count} recorded items were not scoreable.");
            }
        }

        foreach (var flag in snapshot.ReviewFlags)
        {
            lines.Add($"Review flag {flag.Code} ({flag.Severity}): {flag.Message}");
        }

        void SectionState(string name, ReportAssessmentState state)
        {
            if (state != ReportAssessmentState.Assessed)
            {
                lines.Add($"{name}: {Humanize(state.ToString())}.");
            }
        }

        SectionState("Executive summary", content.ExecutiveSummary.State);
        SectionState("Industrial data and systems", content.IndustrialDataAndSystems.State);
        SectionState("AI opportunities and ROI", content.AiOpportunitiesAndRoi.State);
        SectionState("Technical gaps and architecture", content.TechnicalGapsAndArchitecture.State);
        SectionState("Implementation roadmap", content.ImplementationRoadmap.State);
        SectionState("Executive presentation", content.ExecutivePresentation.State);

        var allFindings = (content.ExecutiveSummary.Findings ?? Array.Empty<ReportFinding>())
            .Concat(content.IndustrialDataAndSystems.Findings ?? Array.Empty<ReportFinding>())
            .Concat(content.AiOpportunitiesAndRoi.Findings ?? Array.Empty<ReportFinding>())
            .ToList();
        var preliminary = allFindings.Count(item => item.Classification == ReportFindingClassification.PreliminaryFinding);
        var unsupported = allFindings.Count(item => item.Classification == ReportFindingClassification.UnsupportedClaim);
        var unverified = allFindings.Count(item => item.VerificationStatus == ReportVerificationStatus.Unverified);
        if (preliminary > 0) lines.Add($"{preliminary} finding(s) are classified as preliminary.");
        if (unsupported > 0) lines.Add($"{unsupported} finding(s) are classified as unsupported claims.");
        if (unverified > 0) lines.Add($"{unverified} finding(s) are unverified.");

        foreach (var opportunity in content.AiOpportunitiesAndRoi.Opportunities ?? Array.Empty<AiImplementationOpportunity>())
        {
            foreach (var risk in opportunity.RisksAndAssumptions ?? Array.Empty<string>())
            {
                lines.Add($"Opportunity '{opportunity.ProposedUseCase}': {risk}");
            }
        }

        foreach (var item in content.ImplementationRoadmap.Items ?? Array.Empty<RoadmapItem>())
        {
            foreach (var assumption in item.Assumptions ?? Array.Empty<string>())
            {
                lines.Add($"Roadmap item '{item.Title}': {assumption}");
            }
        }

        container.Column(column =>
        {
            column.Spacing(4);
            column.Item().Text("The limitations below are those recorded in this revision's frozen data.").FontColor(Muted);
            if (lines.Count == 0)
            {
                column.Item().Text("No limitations are recorded in this revision.").Italic().FontColor(Muted);
            }

            foreach (var line in lines)
            {
                column.Item().Text($"- {line}");
            }
        });
    }

    private static void NextSteps(IContainer container, ConsultantReportContent content)
    {
        var decisions = content.ExecutivePresentation.DecisionsRequested;
        var roadmap = content.ImplementationRoadmap;

        container.Column(column =>
        {
            column.Spacing(4);
            if (decisions is { Count: > 0 })
            {
                column.Item().Text("Decisions requested").Bold();
                foreach (var decision in decisions)
                {
                    column.Item().Text($"- {decision.Text}");
                }
            }

            if (roadmap.Items is { Count: > 0 })
            {
                column.Item().PaddingTop(4).Text("Roadmap actions").Bold();
                foreach (var item in OrderedRoadmap(roadmap))
                {
                    column.Item().Text($"- Priority {item.Priority}: {item.Title} (days {item.StartDay} to {item.EndDay})");
                }
            }

            if (decisions is not { Count: > 0 } && roadmap.Items is not { Count: > 0 })
            {
                column.Item().Text("No decisions or roadmap actions have been recorded for this revision. Recommended next step: review the readiness findings with your Sentinel consultant and agree the priorities.");
            }
        });
    }

    private static void Issuance(IContainer container, ReportPdfRenderModel model)
    {
        var rows = new List<(string, string)>
        {
            ("Report identifier", model.ReportId.ToString("D")),
            ("Revision identifier", model.RevisionId.ToString("D")),
            ("Revision number", model.RevisionNumber.ToString(CultureInfo.InvariantCulture)),
            ("Report template", model.TemplateVersion),
            ("Scoring contract version", Text(model.ScoringContractVersion, "Not recorded")),
            ("Scoring snapshot schema version", model.ScoringSnapshotSchemaVersion),
            ("Consultant content schema version", model.ContentSchemaVersion),
            ("Issued", FormatDate(model.IssuedUtc)),
            ("Issued by (user id)", Text(model.IssuedByUserId, "Not recorded")),
            ("Approved", model.ApprovedUtc is { } approved ? FormatDate(approved) : "Not recorded"),
            ("Approved by (user id)", Text(model.ApprovedByUserId, "Not recorded"))
        };

        if (model.BasedOnRevisionId is { } basedOn)
        {
            rows.Add(("Corrects revision", basedOn.ToString("D")));
            rows.Add(("Correction reason", Text(model.CorrectionReason, "Not recorded")));
        }

        container.Element(c => KeyValueTable(c, rows));
    }

    // -------------------------------------------------------------- helpers

    private static IEnumerable<RoadmapItem> OrderedRoadmap(ImplementationRoadmapSection roadmap)
        => (roadmap.Items ?? Array.Empty<RoadmapItem>())
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.StartDay)
            .ThenBy(item => item.EndDay)
            .ThenBy(item => item.Id, StringComparer.Ordinal);

    private static void StateLine(ColumnDescriptor column, ReportAssessmentState state)
    {
        if (state != ReportAssessmentState.Assessed)
        {
            column.Item().Text($"Section state: {Humanize(state.ToString())}.").Italic().FontColor(Muted);
        }
    }

    private static void OptionalLine(ColumnDescriptor card, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            card.Item().Text(text => LabelValue(text, label, value));
        }
    }

    private static void ListLine(ColumnDescriptor card, string label, IReadOnlyList<string>? values)
    {
        if (values is { Count: > 0 })
        {
            card.Item().Text(text => LabelValue(text, label, string.Join("; ", values)));
        }
    }

    private static void EvidenceBlock(
        ColumnDescriptor card,
        IReadOnlyList<ReportEvidenceReference>? references,
        ReadinessScoringSnapshot snapshot)
    {
        card.Item().PaddingTop(3).Text("Evidence").FontSize(8.5f).Bold().FontColor(Muted);
        if (references is null || references.Count == 0)
        {
            card.Item().Text("No evidence references are recorded.").FontSize(8.5f).Italic().FontColor(Muted);
            return;
        }

        foreach (var reference in references)
        {
            card.Item().Text($"- {DescribeEvidence(reference, snapshot)}").FontSize(8.5f);
        }
    }

    internal static string DescribeEvidence(ReportEvidenceReference reference, ReadinessScoringSnapshot snapshot)
    {
        switch (reference.Kind)
        {
            case ReportEvidenceKind.ScoringDimension:
            {
                var dimension = snapshot.Dimensions.FirstOrDefault(item => item.DimensionId == reference.ReferenceId);
                if (dimension is null) return $"Scoring dimension {reference.ReferenceId} (not in frozen snapshot)";
                var score = dimension.Status == ReadinessDimensionStatus.Eligible && dimension.DisplayScore0To100 is not null
                    ? $"score {dimension.DisplayScore0To100}/100" : "score withheld";
                return $"Scoring dimension: {dimension.Name}, {score}";
            }
            case ReportEvidenceKind.ScoringReviewFlag:
            {
                var flag = snapshot.ReviewFlags.FirstOrDefault(item => item.Code == reference.ReferenceId);
                return flag is null
                    ? $"Review flag {reference.ReferenceId} (not in frozen snapshot)"
                    : $"Review flag {flag.Code}: {flag.Message}";
            }
            case ReportEvidenceKind.ConsultantObservation:
                return $"Consultant observation {reference.ReferenceId}";
            default:
            {
                var item = snapshot.Dimensions.SelectMany(dimension => dimension.Items).FirstOrDefault(candidate =>
                    candidate.QuestionId == reference.QuestionId
                    && candidate.FieldId == reference.FieldId
                    && (reference.ItemKey is null || candidate.ItemKey == reference.ItemKey));
                var label = reference.Kind == ReportEvidenceKind.ScoringItem ? "Scoring item" : "Questionnaire answer";
                if (item is null)
                {
                    return $"{label} {reference.ReferenceId} (value not frozen in this revision)";
                }

                var score = item.Score0To4 is { } value ? $", score {value}/4" : string.Empty;
                var wording = ReportEvidenceCatalog.Find(snapshot.QuestionnaireVersion, item.QuestionId, item.FieldId, item.RawValue);
                if (wording is null)
                {
                    return $"{label} {reference.ReferenceId}: recorded value '{item.RawValue ?? "none"}'{score} ({Humanize(item.Status.ToString())}). Question wording is not available for questionnaire version {snapshot.QuestionnaireVersion}.";
                }

                var answer = wording.AnswerLabel ?? $"recorded value '{item.RawValue ?? "none"}'";
                return $"\"{wording.Question}\" Answer: {answer}{score} ({Humanize(item.Status.ToString())}). Ref {reference.ReferenceId}, value '{item.RawValue ?? "none"}', questionnaire {snapshot.QuestionnaireVersion}.";
            }
        }
    }

    private static string DescribeEstimate(EffortCostEstimate estimate)
    {
        if (estimate.Status != ReportEstimateStatus.Estimated)
        {
            return "Not estimated";
        }

        var parts = new List<string>();
        if (estimate.EffortValue is { } effort)
        {
            parts.Add($"effort {effort.ToString("0.##", CultureInfo.InvariantCulture)} {estimate.EffortUnit}".TrimEnd());
        }

        if (estimate.CostAmount is { } cost)
        {
            parts.Add($"cost {cost.ToString("0.##", CultureInfo.InvariantCulture)} {estimate.Currency}".TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(estimate.Basis))
        {
            parts.Add($"basis: {estimate.Basis}");
        }

        return parts.Count == 0 ? "Estimated (no values recorded)" : string.Join("; ", parts);
    }

    private static string DescribeScope(ImplementationScope scope)
    {
        if (scope.State != ReportAssessmentState.Assessed)
        {
            return Humanize(scope.State.ToString());
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(scope.Description)) parts.Add(scope.Description!);
        if (scope.InScopeItems is { Count: > 0 }) parts.Add($"in scope: {string.Join(", ", scope.InScopeItems)}");
        if (scope.OutOfScopeItems is { Count: > 0 }) parts.Add($"out of scope: {string.Join(", ", scope.OutOfScopeItems)}");
        return parts.Count == 0 ? "Assessed (no detail recorded)" : string.Join("; ", parts);
    }

    private static void KeyValueTable(IContainer container, IEnumerable<(string Key, string Value)> rows)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(2f);
                columns.RelativeColumn(3.5f);
            });

            foreach (var (key, value) in rows)
            {
                table.Cell().Element(BodyCell).Text(key).Bold();
                table.Cell().Element(BodyCell).Text(value);
            }
        });
    }

    private static IContainer HeaderCell(IContainer container)
        => container.Background(Navy).PaddingVertical(4).PaddingHorizontal(5);

    private static IContainer BodyCell(IContainer container)
        => container.BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(4).PaddingHorizontal(5);

    private static void LabelValue(TextDescriptor text, string label, string value)
    {
        text.Span($"{label}: ").Bold();
        text.Span(value);
    }

    private static string Text(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value!;

    private static string FormatDate(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Humanize(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append(' ').Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
