using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelMfgSuite.Web.Models.Assessments.Questionnaire
{
    // Versioned response contract for Questionnaire v1.0
    public class QuestionnaireResponseV1
    {
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; set; } = "1.0";

        // Answers keyed by stable question IDs (q01..q30).
        // Presence of a key indicates the customer provided an answer (possibly explicit Unknown).
        // Absence of a key indicates a missing answer (draft state).
        [JsonPropertyName("answers")]
        public Dictionary<string, QuestionAnswer> Answers { get; set; } = new();
    }

    // Generic container for a question's answer. Only one representation should be populated
    // depending on the question type defined in QuestionnaireV1.
    public class QuestionAnswer
    {
        // For free-form text answers (text, longtext)
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        // For single-select answers: stored choice value (e.g. "yes", "unknown")
        [JsonPropertyName("selected")]
        public string? Selected { get; set; }

        // For multi-select answers: array of selected choice values
        [JsonPropertyName("selectedValues")]
        public List<string>? SelectedValues { get; set; }

        // For optional supplemental text values attached to single-select and multi-select questions.
        [JsonPropertyName("supplemental")]
        public Dictionary<string, string?>? Supplemental { get; set; }

        // For composite answers: map of field id to a FieldAnswer
        [JsonPropertyName("composite")]
        public Dictionary<string, FieldAnswer>? Composite { get; set; }

        // For repeatable answers: array of structured records (each record is a map of field id to FieldAnswer)
        [JsonPropertyName("repeatable")]
        public List<Dictionary<string, FieldAnswer>>? Repeatable { get; set; }
    }

    // Field-level answer container reused for composite and repeatable records.
    public class FieldAnswer
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("selected")]
        public string? Selected { get; set; }

        [JsonPropertyName("selectedValues")]
        public List<string>? SelectedValues { get; set; }
    }

    // Helper to serialize with System.Text.Json using recommended settings for this contract.
    public static class QuestionnaireResponseV1Serializer
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };

        public static string Serialize(QuestionnaireResponseV1 response)
            => JsonSerializer.Serialize(response, Options);

        public static QuestionnaireResponseV1? Deserialize(string json)
            => JsonSerializer.Deserialize<QuestionnaireResponseV1>(json, Options);
    }
}
