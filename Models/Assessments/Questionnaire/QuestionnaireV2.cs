using System;
using System.Collections.Generic;
using System.Linq;

namespace SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

public static class QuestionnaireV2
{
    public static readonly QuestionnaireDefinition Definition = CreateDefinition();

    private static QuestionnaireDefinition CreateDefinition()
    {
        var v1 = QuestionnaireV1.Definition;
        var sections = new List<SectionDefinition>(v1.Sections.Count);

        foreach (var section in v1.Sections)
        {
            var questions = section.Questions
                .Select(question => CloneQuestion(question, GetV2DisplayNumber(question.Id)))
                .ToList();
            if (string.Equals(section.Id, "analytics_ai", StringComparison.Ordinal))
            {
                questions.Add(new QuestionDefinition(
                    "q31",
                    "How consistently do operational reports and KPIs reflect actual production conditions?",
                    QuestionType.SingleSelect,
                    choices: new[]
                    {
                        Choice.O("consistently_accurate", "Consistently accurate and trusted"),
                        Choice.O("generally_accurate", "Generally accurate with occasional discrepancies"),
                        Choice.O("frequent_discrepancies", "Frequent discrepancies requiring manual reconciliation"),
                        Choice.O("reliability_not_established", "Reliability has not been established"),
                        Choice.Unknown()
                    },
                    isOptional: true,
                    displayNumber: "Q27"));

                questions.Add(new QuestionDefinition(
                    "q32",
                    "How are operational data and analytics currently used to support decisions?",
                    QuestionType.SingleSelect,
                    choices: new[]
                    {
                        Choice.O("proactive_decisions", "Routinely used for proactive decisions"),
                        Choice.O("investigate_improve", "Regularly used to investigate and improve performance"),
                        Choice.O("retrospective_reporting", "Primarily used for retrospective reporting"),
                        Choice.O("limited_ad_hoc", "Limited or ad hoc use"),
                        Choice.O("not_used", "Not currently used"),
                        Choice.Unknown()
                    },
                    isOptional: true,
                    displayNumber: "Q28"));
            }

            sections.Add(new SectionDefinition(section.Id, section.Title, section.Description, questions));
        }

        return new QuestionnaireDefinition
        {
            SchemaVersion = "2.0",
            Title = "Sentinel Industrial Data & AI Readiness Assessment - Phase 2",
            Description = "Phase 2 questionnaire capturing business priorities, systems, data quality, reporting reliability, and operational decision-making.",
            CreatedUtc = DateTime.UtcNow,
            Sections = sections
        };
    }

    private static string GetV2DisplayNumber(string questionId)
        => questionId switch
        {
            "q27" => "Q29",
            "q28" => "Q30",
            "q29" => "Q31",
            "q30" => "Q32",
            _ => questionId.ToUpperInvariant()
        };

    private static QuestionDefinition CloneQuestion(QuestionDefinition question, string displayNumber)
        => new(
            question.Id,
            question.Text,
            question.Type,
            CloneChoices(question.Choices),
            question.IsOptional,
            question.VisibilityCondition,
            question.MinSelections,
            question.MaxSelections,
            question.RequiresDiscovery,
            question.RepeatableFields?.Select(field => new FieldDefinition(
                field.Id,
                field.Label,
                field.Type,
                field.IsOptional,
                CloneChoices(field.ChoicesArray))).ToArray(),
            question.MaxOccurrences,
            question.SupplementalFields?.Select(field => new SupplementalFieldDefinition(
                field.Id,
                field.Label,
                field.IsOptional,
                field.TriggerChoiceValue)).ToArray(),
            displayNumber);

    private static Choice[]? CloneChoices(IEnumerable<Choice>? choices)
        => choices?.Select(choice => new Choice
        {
            Value = choice.Value,
            Label = choice.Label,
            IsUnknown = choice.IsUnknown,
            RequiresDiscovery = choice.RequiresDiscovery
        }).ToArray();
}
