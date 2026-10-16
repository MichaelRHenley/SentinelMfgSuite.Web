using System;
using System.Collections.Generic;
using System.Linq;

namespace SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

public static class QuestionnaireResponseV1Sanitizer
{
    public static QuestionnaireResponseV1 SanitizeForClient(QuestionnaireDefinition definition, QuestionnaireResponseV1? source)
    {
        var sanitized = new QuestionnaireResponseV1
        {
            SchemaVersion = definition.SchemaVersion,
            Answers = new Dictionary<string, QuestionAnswer>(StringComparer.Ordinal)
        };

        if (source?.Answers is null || source.Answers.Count == 0)
        {
            return sanitized;
        }

        var questionsById = definition.Sections
            .SelectMany(section => section.Questions)
            .ToDictionary(question => question.Id, StringComparer.Ordinal);

        foreach (var answerEntry in source.Answers)
        {
            if (!questionsById.TryGetValue(answerEntry.Key, out var question) || answerEntry.Value is null)
            {
                continue;
            }

            var sanitizedAnswer = SanitizeQuestionAnswer(question, answerEntry.Value);
            if (sanitizedAnswer is not null)
            {
                sanitized.Answers[question.Id] = sanitizedAnswer;
            }
        }

        return sanitized;
    }

    private static QuestionAnswer? SanitizeQuestionAnswer(QuestionDefinition question, QuestionAnswer answer)
    {
        return question.Type switch
        {
            QuestionType.Text or QuestionType.LongText => SanitizeTextAnswer(answer),
            QuestionType.SingleSelect => SanitizeSingleSelectAnswer(question, answer),
            QuestionType.MultiSelect => SanitizeMultiSelectAnswer(question, answer),
            QuestionType.Composite => SanitizeCompositeAnswer(question, answer),
            QuestionType.Repeatable => SanitizeRepeatableAnswer(question, answer),
            _ => null
        };
    }

    private static QuestionAnswer? SanitizeTextAnswer(QuestionAnswer answer)
    {
        if (string.IsNullOrEmpty(answer.Text))
        {
            return null;
        }

        return new QuestionAnswer
        {
            Text = answer.Text
        };
    }

    private static QuestionAnswer? SanitizeSingleSelectAnswer(QuestionDefinition question, QuestionAnswer answer)
    {
        if (string.IsNullOrWhiteSpace(answer.Selected))
        {
            return null;
        }

        if (!IsAllowedChoice(question.Choices, answer.Selected))
        {
            return null;
        }

        var supplemental = SanitizeSupplemental(question, answer.Supplemental, answer.Selected, null);

        return new QuestionAnswer
        {
            Selected = answer.Selected,
            Supplemental = supplemental
        };
    }

    private static QuestionAnswer? SanitizeMultiSelectAnswer(QuestionDefinition question, QuestionAnswer answer)
    {
        var values = SanitizeSelectedValues(question.Choices, answer.SelectedValues);
        if (values.Count == 0)
        {
            return null;
        }

        var supplemental = SanitizeSupplemental(question, answer.Supplemental, null, values);

        return new QuestionAnswer
        {
            SelectedValues = values,
            Supplemental = supplemental
        };
    }

    private static QuestionAnswer? SanitizeCompositeAnswer(QuestionDefinition question, QuestionAnswer answer)
    {
        var composite = SanitizeCompositeRecord(question, answer.Composite);
        if (composite.Count == 0)
        {
            return null;
        }

        return new QuestionAnswer
        {
            Composite = composite
        };
    }

    private static QuestionAnswer? SanitizeRepeatableAnswer(QuestionDefinition question, QuestionAnswer answer)
    {
        if (answer.Repeatable is null || answer.Repeatable.Count == 0)
        {
            return null;
        }

        var maxOccurrences = question.MaxOccurrences.GetValueOrDefault(int.MaxValue);
        var sanitizedRecords = new List<Dictionary<string, FieldAnswer>>();

        foreach (var record in answer.Repeatable)
        {
            if (sanitizedRecords.Count >= maxOccurrences)
            {
                break;
            }

            var sanitizedRecord = SanitizeCompositeRecord(question, record);
            if (sanitizedRecord.Count == 0)
            {
                continue;
            }

            sanitizedRecords.Add(sanitizedRecord);
        }

        if (sanitizedRecords.Count == 0)
        {
            return null;
        }

        return new QuestionAnswer
        {
            Repeatable = sanitizedRecords
        };
    }

    private static Dictionary<string, FieldAnswer> SanitizeCompositeRecord(QuestionDefinition question, Dictionary<string, FieldAnswer>? record)
    {
        var sanitized = new Dictionary<string, FieldAnswer>(StringComparer.Ordinal);
        if (record is null || record.Count == 0)
        {
            return sanitized;
        }

        var fieldsById = (question.RepeatableFields ?? Array.Empty<FieldDefinition>())
            .ToDictionary(field => field.Id, StringComparer.Ordinal);

        foreach (var fieldEntry in record)
        {
            if (!fieldsById.TryGetValue(fieldEntry.Key, out var fieldDefinition) || fieldEntry.Value is null)
            {
                continue;
            }

            var fieldAnswer = SanitizeFieldAnswer(fieldDefinition, fieldEntry.Value);
            if (fieldAnswer is not null)
            {
                sanitized[fieldDefinition.Id] = fieldAnswer;
            }
        }

        return sanitized;
    }

    private static FieldAnswer? SanitizeFieldAnswer(FieldDefinition definition, FieldAnswer answer)
    {
        return definition.Type switch
        {
            QuestionType.Text or QuestionType.LongText => string.IsNullOrEmpty(answer.Text)
                ? null
                : new FieldAnswer { Text = answer.Text },
            QuestionType.SingleSelect => string.IsNullOrWhiteSpace(answer.Selected) || !IsAllowedChoice(definition.ChoicesArray, answer.Selected)
                ? null
                : new FieldAnswer { Selected = answer.Selected },
            QuestionType.MultiSelect => SanitizeSelectedValues(definition.ChoicesArray, answer.SelectedValues) is var selectedValues && selectedValues.Count > 0
                ? new FieldAnswer { SelectedValues = selectedValues }
                : null,
            _ => null
        };
    }

    private static List<string> SanitizeSelectedValues(IReadOnlyCollection<Choice>? choices, List<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return new List<string>();
        }

        var allowed = BuildAllowedChoiceSet(choices);
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value) && allowed.Contains(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<string, string?>? SanitizeSupplemental(
        QuestionDefinition question,
        Dictionary<string, string?>? supplemental,
        string? selected,
        List<string>? selectedValues)
    {
        if (supplemental is null || supplemental.Count == 0 || question.SupplementalFields is null || question.SupplementalFields.Length == 0)
        {
            return null;
        }

        var sanitized = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in question.SupplementalFields)
        {
            if (!supplemental.TryGetValue(field.Id, out var value))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(field.TriggerChoiceValue) && !IsTriggered(field.TriggerChoiceValue, selected, selectedValues))
            {
                continue;
            }

            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            sanitized[field.Id] = value;
        }

        return sanitized.Count == 0 ? null : sanitized;
    }

    private static bool IsTriggered(string triggerValue, string? selected, List<string>? selectedValues)
    {
        if (string.Equals(selected, triggerValue, StringComparison.Ordinal))
        {
            return true;
        }

        return selectedValues?.Contains(triggerValue, StringComparer.Ordinal) == true;
    }

    private static bool IsAllowedChoice(IReadOnlyCollection<Choice>? choices, string value)
    {
        if (choices is null || choices.Count == 0)
        {
            return !string.IsNullOrWhiteSpace(value);
        }

        return BuildAllowedChoiceSet(choices).Contains(value);
    }

    private static HashSet<string> BuildAllowedChoiceSet(IReadOnlyCollection<Choice>? choices)
    {
        if (choices is null || choices.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return choices
            .Where(choice => !string.IsNullOrWhiteSpace(choice.Value))
            .Select(choice => choice.Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
