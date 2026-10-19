using System.Collections.Generic;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class QuestionnaireResponseV1SanitizerTests
{
    [Fact]
    public void SanitizeForClient_Removes_Unknown_Question_And_Unexpected_Fields()
    {
        var source = new QuestionnaireResponseV1
        {
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q04"] = new QuestionAnswer { Selected = "11-25" },
                ["q99"] = new QuestionAnswer { Text = "should-not-survive" }
            }
        };

        var sanitized = QuestionnaireResponseV1Sanitizer.SanitizeForClient(QuestionnaireV1.Definition, source);

        Assert.True(sanitized.Answers.ContainsKey("q04"));
        Assert.False(sanitized.Answers.ContainsKey("q99"));
        Assert.Equal(QuestionnaireV1.Definition.SchemaVersion, sanitized.SchemaVersion);
    }

    [Fact]
    public void SanitizeForClient_Preserves_Explicit_Unknown_Selection()
    {
        var source = new QuestionnaireResponseV1
        {
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q14"] = new QuestionAnswer { SelectedValues = new List<string> { "unknown" } }
            }
        };

        var sanitized = QuestionnaireResponseV1Sanitizer.SanitizeForClient(QuestionnaireV1.Definition, source);

        Assert.True(sanitized.Answers.ContainsKey("q14"));
        Assert.Equal("unknown", Assert.Single(sanitized.Answers["q14"].SelectedValues!));
    }

    [Fact]
    public void SanitizeForClient_Constrains_Repeatable_Records_To_MaxOccurrences_And_Known_Fields()
    {
        var source = new QuestionnaireResponseV1
        {
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q13"] = new QuestionAnswer
                {
                    Repeatable = new List<Dictionary<string, FieldAnswer>>
                    {
                        new()
                        {
                            ["source_name"] = new FieldAnswer { Text = "Source 1" },
                            ["system_type"] = new FieldAnswer { Selected = "plc" },
                            ["bad_field"] = new FieldAnswer { Text = "remove" }
                        },
                        new() { ["source_name"] = new FieldAnswer { Text = "Source 2" } },
                        new() { ["source_name"] = new FieldAnswer { Text = "Source 3" } },
                        new() { ["source_name"] = new FieldAnswer { Text = "Source 4" } },
                        new() { ["source_name"] = new FieldAnswer { Text = "Source 5" } },
                        new() { ["source_name"] = new FieldAnswer { Text = "Source 6" } }
                    }
                }
            }
        };

        var sanitized = QuestionnaireResponseV1Sanitizer.SanitizeForClient(QuestionnaireV1.Definition, source);

        var repeatable = sanitized.Answers["q13"].Repeatable;
        Assert.NotNull(repeatable);
        Assert.Equal(5, repeatable!.Count);
        Assert.DoesNotContain("bad_field", repeatable[0].Keys);
    }

    [Fact]
    public void SanitizeForClient_Preserves_Q07_Selected_Value_For_Client_Dependent_Validation()
    {
        var source = new QuestionnaireResponseV1
        {
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q07"] = new QuestionAnswer { Selected = "quality" }
            }
        };

        var sanitized = QuestionnaireResponseV1Sanitizer.SanitizeForClient(QuestionnaireV1.Definition, source);

        Assert.True(sanitized.Answers.ContainsKey("q07"));
        Assert.Equal("quality", sanitized.Answers["q07"].Selected);
    }

    [Fact]
    public void SanitizeForClient_V2_Preserves_Valid_Q31_And_Q32_Answers()
    {
        var source = new QuestionnaireResponseV1
        {
            SchemaVersion = "2.0",
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q04"] = new QuestionAnswer { Selected = "11-25" },
                ["q31"] = new QuestionAnswer { Selected = "generally_accurate" },
                ["q32"] = new QuestionAnswer { Selected = "investigate_improve" },
                ["q99"] = new QuestionAnswer { Text = "remove" }
            }
        };

        var sanitized = QuestionnaireResponseV1Sanitizer.SanitizeForClient(QuestionnaireV2.Definition, source);

        Assert.Equal("2.0", sanitized.SchemaVersion);
        Assert.Equal("11-25", sanitized.Answers["q04"].Selected);
        Assert.Equal("generally_accurate", sanitized.Answers["q31"].Selected);
        Assert.Equal("investigate_improve", sanitized.Answers["q32"].Selected);
        Assert.DoesNotContain("q99", sanitized.Answers.Keys);
    }

    [Fact]
    public void SanitizeForClient_V2_Drops_Invalid_Q31_And_Q32_Choices_Without_Rewriting_Source()
    {
        var source = new QuestionnaireResponseV1
        {
            SchemaVersion = "2.0",
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q31"] = new QuestionAnswer { Selected = "invalid_reliability" },
                ["q32"] = new QuestionAnswer { Selected = "invalid_decision_use" }
            }
        };

        var sanitized = QuestionnaireResponseV1Sanitizer.SanitizeForClient(QuestionnaireV2.Definition, source);

        Assert.Empty(sanitized.Answers);
        Assert.Equal("invalid_reliability", source.Answers["q31"].Selected);
        Assert.Equal("invalid_decision_use", source.Answers["q32"].Selected);
    }
}
