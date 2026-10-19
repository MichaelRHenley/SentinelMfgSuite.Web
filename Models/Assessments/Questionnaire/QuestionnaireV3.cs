using System;
using System.Collections.Generic;
using System.Linq;

namespace SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

public static class QuestionnaireV3
{
    public static readonly QuestionnaireDefinition Definition = CreateDefinition();

    private static QuestionnaireDefinition CreateDefinition()
    {
        var sections = new List<SectionDefinition>(QuestionnaireV2.Definition.Sections.Count);
        var displayNumber = 0;

        foreach (var section in QuestionnaireV2.Definition.Sections)
        {
            var questions = section.Questions.Select(question => CloneQuestion(question, ++displayNumber)).ToList();

            if (string.Equals(section.Id, "systems_data", StringComparison.Ordinal))
            {
                questions.Add(CreateQuestion(
                    "q33",
                    "For the equipment and systems relevant to your priority operational challenge, how much of the needed data is available through automatic connections, without routine manual re-entry or file transfers?",
                    new[]
                    {
                        Choice.O("none", "None"),
                        Choice.O("less_than_quarter", "Less than one-quarter"),
                        Choice.O("about_quarter_to_half", "About one-quarter to one-half"),
                        Choice.O("most", "More than one-half, but not all or nearly all"),
                        Choice.O("nearly_all", "All or nearly all"),
                        Choice.Unknown()
                    },
                    ++displayNumber));

                questions.Add(CreateQuestion(
                    "q34",
                    "For those automatic connections, how reliably does data arrive in time for the operational work that depends on it?",
                    new[]
                    {
                        Choice.O("not_available", "No automatic data exchange is available"),
                        Choice.O("often_late_or_incomplete", "Often late, incomplete, or unavailable"),
                        Choice.O("sometimes_late_or_incomplete", "Sometimes late or incomplete"),
                        Choice.O("usually_on_time", "Usually arrives complete and in time"),
                        Choice.O("consistently_reliable", "Consistently complete and timely, with issues identified and addressed"),
                        Choice.Unknown()
                    },
                    ++displayNumber));
            }

            if (string.Equals(section.Id, "data_quality", StringComparison.Ordinal))
            {
                questions.Add(CreateQuestion(
                    "q35",
                    "How are important operational data values checked for accuracy, and how are identified issues handled?",
                    new[]
                    {
                        Choice.O("no_checks", "Values are not routinely checked"),
                        Choice.O("informal_checks", "Issues are usually noticed informally or after they affect work"),
                        Choice.O("some_checks", "Some important values are checked, but coverage or follow-up is inconsistent"),
                        Choice.O("routine_checks", "Important values are routinely checked and issues are followed up"),
                        Choice.O("systematic_monitoring", "Checks cover important data consistently; issues are tracked and addressed"),
                        Choice.Unknown()
                    },
                    ++displayNumber));

                questions.Add(CreateQuestion(
                    "q36",
                    "For the operational data needed to investigate your priority challenge, how often are required readings or records missing?",
                    new[]
                    {
                        Choice.O("frequently_missing", "Missing frequently, with important gaps"),
                        Choice.O("often_missing", "Missing often"),
                        Choice.O("sometimes_missing", "Missing sometimes; impact varies"),
                        Choice.O("rarely_missing", "Rarely missing; gaps are usually identified"),
                        Choice.O("almost_never_missing", "Almost never missing; completeness is monitored"),
                        Choice.Unknown()
                    },
                    ++displayNumber));
            }

            sections.Add(new SectionDefinition(section.Id, section.Title, section.Description, questions));
        }

        return new QuestionnaireDefinition
        {
            SchemaVersion = "3.0",
            Title = "Sentinel Industrial Data & AI Readiness Assessment - Phase 3",
            Description = "Phase 3 questionnaire capturing business priorities, systems, data quality, reporting reliability, and operational decision-making.",
            CreatedUtc = DateTime.UtcNow,
            Sections = sections
        };
    }

    private static QuestionDefinition CreateQuestion(string id, string text, Choice[] choices, int displayNumber)
        => new(id, text, QuestionType.SingleSelect, choices, isOptional: false, displayNumber: $"Q{displayNumber:D2}");

    private static QuestionDefinition CloneQuestion(QuestionDefinition question, int displayNumber)
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
            $"Q{displayNumber:D2}");

    private static Choice[]? CloneChoices(IEnumerable<Choice>? choices)
        => choices?.Select(choice => new Choice
        {
            Value = choice.Value,
            Label = choice.Label,
            IsUnknown = choice.IsUnknown,
            RequiresDiscovery = choice.RequiresDiscovery
        }).ToArray();
}
