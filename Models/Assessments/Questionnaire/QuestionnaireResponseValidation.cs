using System;
using System.Collections.Generic;
using System.Linq;

namespace SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

public enum QuestionnaireValidationMode
{
    Draft,
    Submission,
}

public sealed record QuestionnaireValidationError(string QuestionId, string? FieldId, string Message);

public sealed class QuestionnaireValidationResult
{
    public List<QuestionnaireValidationError> Errors { get; } = new();

    public bool IsValid => Errors.Count == 0;
}

public static class QuestionnaireResponseV1Validator
{
    private static readonly HashSet<string> ExclusiveChoiceValues = new(StringComparer.Ordinal)
    {
        "unknown",
        "none",
        "not_collected",
        "no_known",
    };

    public static QuestionnaireValidationResult Validate(QuestionnaireResponseV1? response, QuestionnaireValidationMode mode)
        => Validate(response, QuestionnaireV1.Definition, mode);

    public static QuestionnaireValidationResult Validate(
        QuestionnaireResponseV1? response,
        QuestionnaireDefinition definition,
        QuestionnaireValidationMode mode)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var result = new QuestionnaireValidationResult();

        if (response is null)
        {
            result.Errors.Add(new QuestionnaireValidationError(string.Empty, null, "Response payload is required."));
            return result;
        }

        if (!string.Equals(response.SchemaVersion, definition.SchemaVersion, StringComparison.Ordinal))
        {
            result.Errors.Add(new QuestionnaireValidationError(string.Empty, null, $"Schema version must be '{definition.SchemaVersion}'."));
        }

        var questionsById = definition.Sections
            .SelectMany(section => section.Questions)
            .ToDictionary(question => question.Id, StringComparer.Ordinal);

        foreach (var answerEntry in response.Answers)
        {
            if (!questionsById.TryGetValue(answerEntry.Key, out var question))
            {
                result.Errors.Add(new QuestionnaireValidationError(answerEntry.Key, null, "Unknown question ID."));
                continue;
            }

            ValidateQuestion(result, question, answerEntry.Value, mode);
        }

        ValidateCrossQuestionRules(result, response, mode);

        if (mode == QuestionnaireValidationMode.Submission)
        {
            ValidateRequiredQuestions(result, response, questionsById);
        }

        return result;
    }

    private static void ValidateQuestion(QuestionnaireValidationResult result, QuestionDefinition question, QuestionAnswer? answer, QuestionnaireValidationMode mode)
    {
        if (answer is null)
        {
            result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Answer payload is required."));
            return;
        }

        switch (question.Type)
        {
            case QuestionType.Text:
            case QuestionType.LongText:
                ValidateTextAnswer(result, question.Id, answer.Text, answer.Selected, answer.SelectedValues, answer.Supplemental, answer.Composite, answer.Repeatable);
                break;
            case QuestionType.SingleSelect:
                ValidateSingleSelectAnswer(result, question, answer.Text, answer.Selected, answer.SelectedValues, answer.Supplemental, answer.Composite, answer.Repeatable);
                break;
            case QuestionType.MultiSelect:
                ValidateMultiSelectAnswer(result, question, answer.Text, answer.Selected, answer.SelectedValues, answer.Supplemental, answer.Composite, answer.Repeatable, mode);
                break;
            case QuestionType.Composite:
                ValidateCompositeAnswer(result, question, answer.Text, answer.Selected, answer.SelectedValues, answer.Supplemental, answer.Composite, answer.Repeatable, mode);
                break;
            case QuestionType.Repeatable:
                ValidateRepeatableAnswer(result, question, answer.Text, answer.Selected, answer.SelectedValues, answer.Supplemental, answer.Composite, answer.Repeatable, mode);
                break;
            default:
                result.Errors.Add(new QuestionnaireValidationError(question.Id, null, $"Unsupported question type '{question.Type}'."));
                break;
        }
    }

    private static void ValidateTextAnswer(
        QuestionnaireValidationResult result,
        string questionId,
        string? text,
        string? selected,
        List<string>? selectedValues,
        Dictionary<string, string?>? supplemental,
        Dictionary<string, FieldAnswer>? composite,
        List<Dictionary<string, FieldAnswer>>? repeatable)
    {
        if (selected is not null || selectedValues is not null || supplemental is not null || composite is not null || repeatable is not null)
        {
            result.Errors.Add(new QuestionnaireValidationError(questionId, null, "Answer shape is invalid for a text question."));
        }

        if (text is null)
        {
            result.Errors.Add(new QuestionnaireValidationError(questionId, null, "Text answer is required when the question is present."));
        }
    }

    private static void ValidateSingleSelectAnswer(
        QuestionnaireValidationResult result,
        QuestionDefinition question,
        string? text,
        string? selected,
        List<string>? selectedValues,
        Dictionary<string, string?>? supplemental,
        Dictionary<string, FieldAnswer>? composite,
        List<Dictionary<string, FieldAnswer>>? repeatable)
    {
        if (text is not null || selectedValues is not null || composite is not null || repeatable is not null)
        {
            result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Answer shape is invalid for a single-select question."));
        }

        ValidateSupplementalAnswers(result, question, selected, null, supplemental);

        if (selected is null)
        {
            result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "A selected value is required when the question is present."));
            return;
        }

        if (string.IsNullOrWhiteSpace(selected))
        {
            result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Selected value cannot be blank."));
            return;
        }

        if (question.Choices is not null && !question.Choices.Any(choice => string.Equals(choice.Value, selected, StringComparison.Ordinal)))
        {
            result.Errors.Add(new QuestionnaireValidationError(question.Id, null, $"'{selected}' is not a supported choice value."));
        }
    }

    private static void ValidateMultiSelectAnswer(
        QuestionnaireValidationResult result,
        QuestionDefinition question,
        string? text,
        string? selected,
        List<string>? selectedValues,
        Dictionary<string, string?>? supplemental,
        Dictionary<string, FieldAnswer>? composite,
        List<Dictionary<string, FieldAnswer>>? repeatable,
        QuestionnaireValidationMode mode)
    {
        if (text is not null || selected is not null || composite is not null || repeatable is not null)
        {
            result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Answer shape is invalid for a multi-select question."));
        }

        ValidateSupplementalAnswers(result, question, null, selectedValues, supplemental);

        if (selectedValues is null)
        {
            result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Selected values are required when the question is present."));
            return;
        }

        if (selectedValues.Count == 0)
        {
            result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Select at least one option or omit the question from the response."));
            return;
        }

        ValidateSelectedValues(result, question.Id, null, selectedValues, question.Choices, question.MaxSelections, mode == QuestionnaireValidationMode.Submission ? question.MinSelections : null);
    }

    private static void ValidateCompositeAnswer(
        QuestionnaireValidationResult result,
        QuestionDefinition question,
        string? text,
        string? selected,
        List<string>? selectedValues,
        Dictionary<string, string?>? supplemental,
        Dictionary<string, FieldAnswer>? composite,
        List<Dictionary<string, FieldAnswer>>? repeatable,
        QuestionnaireValidationMode mode)
    {
        if (text is not null || selected is not null || selectedValues is not null || supplemental is not null || repeatable is not null)
        {
            result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Answer shape is invalid for a composite question."));
        }

        if (composite is null)
        {
            result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Composite fields are required when the question is present."));
            return;
        }

        ValidateCompositeFields(result, question, composite, mode, question.IsOptional ? IsCompositePopulated(composite) : true);
    }

    private static void ValidateRepeatableAnswer(
        QuestionnaireValidationResult result,
        QuestionDefinition question,
        string? text,
        string? selected,
        List<string>? selectedValues,
        Dictionary<string, string?>? supplemental,
        Dictionary<string, FieldAnswer>? composite,
        List<Dictionary<string, FieldAnswer>>? repeatable,
        QuestionnaireValidationMode mode)
    {
        if (text is not null || selected is not null || selectedValues is not null || supplemental is not null || composite is not null)
        {
            result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Answer shape is invalid for a repeatable question."));
        }

        if (repeatable is null)
        {
            result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Repeatable records are required when the question is present."));
            return;
        }

        if (question.MaxOccurrences is int maxOccurrences && repeatable.Count > maxOccurrences)
        {
            result.Errors.Add(new QuestionnaireValidationError(question.Id, null, $"No more than {maxOccurrences} records are allowed."));
        }

        for (var index = 0; index < repeatable.Count; index++)
        {
            var record = repeatable[index];
            var recordPopulated = IsRecordPopulated(record);
            ValidateCompositeFields(result, question, record, mode, enforceRequiredFields: mode == QuestionnaireValidationMode.Submission && recordPopulated, recordIndex: index + 1);
        }
    }

    private static void ValidateSupplementalAnswers(
        QuestionnaireValidationResult result,
        QuestionDefinition question,
        string? selected,
        List<string>? selectedValues,
        Dictionary<string, string?>? supplemental)
    {
        if (supplemental is null)
        {
            return;
        }

        var supportedFields = (question.SupplementalFields ?? Array.Empty<SupplementalFieldDefinition>())
            .ToDictionary(field => field.Id, StringComparer.Ordinal);

        foreach (var supplementalField in supplemental)
        {
            if (!supportedFields.TryGetValue(supplementalField.Key, out var fieldDefinition))
            {
                result.Errors.Add(new QuestionnaireValidationError(question.Id, supplementalField.Key, "Unsupported supplemental field ID."));
                continue;
            }

            if (!string.IsNullOrEmpty(fieldDefinition.TriggerChoiceValue) && !IsTriggerChoiceSelected(fieldDefinition.TriggerChoiceValue, selected, selectedValues))
            {
                result.Errors.Add(new QuestionnaireValidationError(question.Id, supplementalField.Key, $"Supplemental field '{supplementalField.Key}' is only allowed when '{fieldDefinition.TriggerChoiceValue}' is selected."));
            }
        }
    }

    private static bool IsTriggerChoiceSelected(string triggerChoiceValue, string? selected, List<string>? selectedValues)
    {
        if (string.Equals(selected, triggerChoiceValue, StringComparison.Ordinal))
        {
            return true;
        }

        return selectedValues?.Contains(triggerChoiceValue, StringComparer.Ordinal) == true;
    }

    private static void ValidateCompositeFields(
        QuestionnaireValidationResult result,
        QuestionDefinition question,
        Dictionary<string, FieldAnswer> composite,
        QuestionnaireValidationMode mode,
        bool enforceRequiredFields,
        int? recordIndex = null)
    {
        var fieldDefinitions = question.RepeatableFields ?? Array.Empty<FieldDefinition>();
        var fieldsById = fieldDefinitions.ToDictionary(field => field.Id, StringComparer.Ordinal);

        foreach (var answerField in composite)
        {
            if (!fieldsById.TryGetValue(answerField.Key, out var fieldDefinition))
            {
                var message = recordIndex is int index
                    ? $"Record {index} contains an unknown field."
                    : "Unknown field ID.";
                result.Errors.Add(new QuestionnaireValidationError(question.Id, answerField.Key, message));
                continue;
            }

            ValidateFieldAnswer(result, question.Id, fieldDefinition, answerField.Key, answerField.Value, recordIndex);
        }

        if (mode != QuestionnaireValidationMode.Submission || !enforceRequiredFields)
        {
            return;
        }

        foreach (var fieldDefinition in fieldDefinitions.Where(field => !field.IsOptional))
        {
            if (!composite.TryGetValue(fieldDefinition.Id, out var fieldAnswer) || !HasMeaningfulFieldValue(fieldAnswer))
            {
                var message = recordIndex is int index
                    ? $"Record {index} requires '{fieldDefinition.Id}'."
                    : $"Field '{fieldDefinition.Id}' is required.";
                result.Errors.Add(new QuestionnaireValidationError(question.Id, fieldDefinition.Id, message));
            }
        }
    }

    private static void ValidateFieldAnswer(
        QuestionnaireValidationResult result,
        string questionId,
        FieldDefinition fieldDefinition,
        string fieldId,
        FieldAnswer? answer,
        int? recordIndex)
    {
        if (answer is null)
        {
            result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, "Field payload is required.")));
            return;
        }

        switch (fieldDefinition.Type)
        {
            case QuestionType.Text:
            case QuestionType.LongText:
                if (answer.Selected is not null || answer.SelectedValues is not null)
                {
                    result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, "Field shape is invalid for a text field.")));
                }

                if (answer.Text is null)
                {
                    result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, "Text value is required when the field is present.")));
                }
                break;

            case QuestionType.SingleSelect:
                if (answer.Text is not null || answer.SelectedValues is not null)
                {
                    result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, "Field shape is invalid for a single-select field.")));
                }

                if (answer.Selected is null)
                {
                    result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, "Selected value is required when the field is present.")));
                    break;
                }

                if (string.IsNullOrWhiteSpace(answer.Selected))
                {
                    result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, "Selected value cannot be blank.")));
                    break;
                }

                if (fieldDefinition.ChoicesArray is not null && !fieldDefinition.ChoicesArray.Any(choice => string.Equals(choice.Value, answer.Selected, StringComparison.Ordinal)))
                {
                    result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, $"'{answer.Selected}' is not a supported choice value.")));
                }
                break;

            case QuestionType.MultiSelect:
                if (answer.Text is not null || answer.Selected is not null)
                {
                    result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, "Field shape is invalid for a multi-select field.")));
                }

                if (answer.SelectedValues is null)
                {
                    result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, "Selected values are required when the field is present.")));
                    break;
                }

                if (answer.SelectedValues.Count == 0)
                {
                    result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, "Select at least one option.")));
                    break;
                }

                ValidateSelectedValues(result, questionId, fieldId, answer.SelectedValues, fieldDefinition.ChoicesArray, null, null, recordIndex);
                break;

            default:
                result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, $"Unsupported field type '{fieldDefinition.Type}'.")));
                break;
        }
    }

    private static void ValidateSelectedValues(
        QuestionnaireValidationResult result,
        string questionId,
        string? fieldId,
        List<string> selectedValues,
        IEnumerable<Choice>? choices,
        int? maxSelections,
        int? minSelections,
        int? recordIndex = null)
    {
        if (selectedValues.Any(string.IsNullOrWhiteSpace))
        {
            result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, "Selected values cannot contain blanks.")));
        }

        var duplicateValues = selectedValues
            .GroupBy(value => value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicateValues.Length > 0)
        {
            result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, $"Duplicate selected values are not allowed: {string.Join(", ", duplicateValues)}.")));
        }

        var allowedChoices = choices?.Select(choice => choice.Value).ToHashSet(StringComparer.Ordinal);
        if (allowedChoices is not null)
        {
            foreach (var value in selectedValues.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (!allowedChoices.Contains(value))
                {
                    result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, $"'{value}' is not a supported choice value.")));
                }
            }
        }

        if (maxSelections is int max && selectedValues.Count > max)
        {
            result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, $"No more than {max} selections are allowed.")));
        }

        if (minSelections is int min && selectedValues.Count < min)
        {
            result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, $"At least {min} selection{(min == 1 ? string.Empty : "s")} required.")));
        }

        var selectedExclusiveValues = selectedValues
            .Where(value => ExclusiveChoiceValues.Contains(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (selectedExclusiveValues.Length > 0 && selectedValues.Count > selectedExclusiveValues.Length)
        {
            result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, $"Exclusive choice values cannot be combined with other selections: {string.Join(", ", selectedExclusiveValues)}.")));
        }

        if (selectedExclusiveValues.Length > 1)
        {
            result.Errors.Add(new QuestionnaireValidationError(questionId, fieldId, BuildFieldMessage(recordIndex, $"Only one exclusive choice value may be selected: {string.Join(", ", selectedExclusiveValues)}.")));
        }
    }

    private static void ValidateCrossQuestionRules(QuestionnaireValidationResult result, QuestionnaireResponseV1 response, QuestionnaireValidationMode mode)
    {
        response.Answers.TryGetValue("q06", out var q06);
        response.Answers.TryGetValue("q07", out var q07);

        if (q07?.Selected is not null)
        {
            if (q06?.SelectedValues is null || q06.SelectedValues.Count == 0)
            {
                result.Errors.Add(new QuestionnaireValidationError("q07", null, "q07 requires at least one selected challenge in q06."));
            }
            else if (!q06.SelectedValues.Contains(q07.Selected, StringComparer.Ordinal))
            {
                result.Errors.Add(new QuestionnaireValidationError("q07", null, "q07 must match one of the selected q06 challenges."));
            }
        }
        else if (mode == QuestionnaireValidationMode.Submission && q06?.SelectedValues is not null && q06.SelectedValues.Count > 0)
        {
            result.Errors.Add(new QuestionnaireValidationError("q07", null, "q07 is required when q06 has selected challenges."));
        }
    }

    private static void ValidateRequiredQuestions(
        QuestionnaireValidationResult result,
        QuestionnaireResponseV1 response,
        IReadOnlyDictionary<string, QuestionDefinition> questionsById)
    {
        foreach (var question in questionsById.Values.Where(question => !question.IsOptional))
        {
            if (!response.Answers.TryGetValue(question.Id, out var answer))
            {
                result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Question is required for submission."));
                continue;
            }

            switch (question.Type)
            {
                case QuestionType.Text:
                case QuestionType.LongText:
                    if (string.IsNullOrWhiteSpace(answer.Text))
                    {
                        result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Text answer is required for submission."));
                    }
                    break;

                case QuestionType.SingleSelect:
                    if (string.IsNullOrWhiteSpace(answer.Selected))
                    {
                        result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "A selected value is required for submission."));
                    }
                    break;

                case QuestionType.MultiSelect:
                    if (answer.SelectedValues is null || answer.SelectedValues.Count == 0)
                    {
                        result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "At least one selection is required for submission."));
                    }
                    break;

                case QuestionType.Composite:
                    if (answer.Composite is null)
                    {
                        result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Composite fields are required for submission."));
                    }
                    break;

                case QuestionType.Repeatable:
                    if (answer.Repeatable is null)
                    {
                        result.Errors.Add(new QuestionnaireValidationError(question.Id, null, "Repeatable records are required for submission."));
                    }
                    break;
            }
        }
    }

    private static bool IsCompositePopulated(Dictionary<string, FieldAnswer> composite)
        => composite.Count > 0;

    private static bool IsRecordPopulated(Dictionary<string, FieldAnswer> record)
        => record.Count > 0 && record.Values.Any(HasMeaningfulFieldValue);

    private static bool HasMeaningfulFieldValue(FieldAnswer? answer)
    {
        if (answer is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(answer.Text) || !string.IsNullOrWhiteSpace(answer.Selected))
        {
            return true;
        }

        return answer.SelectedValues is { Count: > 0 };
    }

    private static string BuildFieldMessage(int? recordIndex, string message)
        => recordIndex is int index ? $"Record {index}: {message}" : message;
}
