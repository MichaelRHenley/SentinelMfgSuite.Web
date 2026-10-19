using System.Linq;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class QuestionnaireDefinitionResolverTests
{
    [Fact]
    public void Resolve_For_1_0_Returns_QuestionnaireV1_Definition()
    {
        var definition = QuestionnaireDefinitionResolver.Resolve("1.0");

        Assert.Same(QuestionnaireV1.Definition, definition);
    }

    [Fact]
    public void V1_Remains_Exactly_30_Questions()
    {
        Assert.Equal(30, QuestionnaireV1.Definition.Sections.Sum(section => section.Questions.Count));
        Assert.Equal(Enumerable.Range(1, 30).Select(number => $"Q{number:D2}"),
            QuestionnaireV1.Definition.Sections.SelectMany(section => section.Questions).Select(question => question.DisplayNumber));
    }

    [Fact]
    public void V2_Has_Six_Sections_And_32_Unique_Question_Ids()
    {
        var questions = QuestionnaireV2.Definition.Sections.SelectMany(section => section.Questions).ToList();

        Assert.Equal("2.0", QuestionnaireV2.Definition.SchemaVersion);
        Assert.Equal(6, QuestionnaireV2.Definition.Sections.Count);
        Assert.Equal(32, questions.Count);
        Assert.Equal(32, questions.Select(question => question.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enumerable.Range(1, 32).Select(number => $"Q{number:D2}"), questions.Select(question => question.DisplayNumber));
        Assert.Equal("q23", questions.Single(question => question.DisplayNumber == "Q23").Id);
        Assert.Equal("q26", questions.Single(question => question.DisplayNumber == "Q26").Id);
        Assert.Equal("q31", questions.Single(question => question.DisplayNumber == "Q27").Id);
        Assert.Equal("q32", questions.Single(question => question.DisplayNumber == "Q28").Id);
        Assert.Equal("q27", questions.Single(question => question.DisplayNumber == "Q29").Id);
        Assert.Equal("q30", questions.Single(question => question.DisplayNumber == "Q32").Id);
    }

    [Fact]
    public void V2_Preserves_All_Inherited_Question_Definitions_And_Section_Assignments()
    {
        var v1Sections = QuestionnaireV1.Definition.Sections;
        var v2Sections = QuestionnaireV2.Definition.Sections;

        Assert.Equal(v1Sections.Select(section => section.Id), v2Sections.Select(section => section.Id));

        foreach (var v1Section in v1Sections)
        {
            var v2Section = Assert.Single(v2Sections.Where(section => section.Id == v1Section.Id));
            foreach (var v1Question in v1Section.Questions)
            {
                var v2Question = Assert.Single(v2Section.Questions.Where(question => question.Id == v1Question.Id));
                AssertQuestionDefinitionsEqual(v1Question, v2Question);
            }
        }

        Assert.NotSame(v1Sections[0].Questions[0], v2Sections[0].Questions[0]);
        Assert.NotSame(v1Sections[0].Questions[2].Choices, v2Sections[0].Questions[2].Choices);
        Assert.NotSame(v1Sections[0].Questions[2].Choices![0], v2Sections[0].Questions[2].Choices![0]);
    }

    [Fact]
    public void V2_Adds_Optional_Questions_After_Q26_In_Section_5_With_Expected_Choices()
    {
        var section = Assert.Single(QuestionnaireV2.Definition.Sections.Where(section => section.Id == "analytics_ai"));
        var q26Index = section.Questions.FindIndex(question => question.Id == "q26");
        var q31 = section.Questions[q26Index + 1];
        var q32 = section.Questions[q26Index + 2];

        Assert.Equal("q31", q31.Id);
        Assert.Equal("How consistently do operational reports and KPIs reflect actual production conditions?", q31.Text);
        Assert.Equal(QuestionType.SingleSelect, q31.Type);
        Assert.True(q31.IsOptional);
        AssertChoices(q31, new[]
        {
            ("consistently_accurate", "Consistently accurate and trusted", false),
            ("generally_accurate", "Generally accurate with occasional discrepancies", false),
            ("frequent_discrepancies", "Frequent discrepancies requiring manual reconciliation", false),
            ("reliability_not_established", "Reliability has not been established", false),
            ("unknown", "Unknown / Not sure", true)
        });

        Assert.Equal("q32", q32.Id);
        Assert.Equal("How are operational data and analytics currently used to support decisions?", q32.Text);
        Assert.Equal(QuestionType.SingleSelect, q32.Type);
        Assert.True(q32.IsOptional);
        AssertChoices(q32, new[]
        {
            ("proactive_decisions", "Routinely used for proactive decisions", false),
            ("investigate_improve", "Regularly used to investigate and improve performance", false),
            ("retrospective_reporting", "Primarily used for retrospective reporting", false),
            ("limited_ad_hoc", "Limited or ad hoc use", false),
            ("not_used", "Not currently used", false),
            ("unknown", "Unknown / Not sure", true)
        });
    }

    [Fact]
    public void V3_Has_Six_Sections_And_36_Questions_With_Sequential_Display_Numbers()
    {
        var sections = QuestionnaireV3.Definition.Sections;
        var questions = sections.SelectMany(section => section.Questions).ToList();

        Assert.Equal("3.0", QuestionnaireV3.Definition.SchemaVersion);
        Assert.Equal(6, sections.Count);
        Assert.Equal(36, questions.Count);
        Assert.Equal(36, questions.Select(question => question.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enumerable.Range(1, 36).Select(number => $"Q{number:D2}"), questions.Select(question => question.DisplayNumber));

        var systems = Assert.Single(sections.Where(section => section.Id == "systems_data"));
        Assert.Equal(new[] { "q33", "q34" }, systems.Questions.Where(question => question.Id is "q33" or "q34").Select(question => question.Id));
        var quality = Assert.Single(sections.Where(section => section.Id == "data_quality"));
        Assert.Equal(new[] { "q35", "q36" }, quality.Questions.Where(question => question.Id is "q35" or "q36").Select(question => question.Id));

        Assert.Equal("q33", questions.Single(question => question.DisplayNumber == "Q17").Id);
        Assert.Equal("q34", questions.Single(question => question.DisplayNumber == "Q18").Id);
        Assert.Equal("q35", questions.Single(question => question.DisplayNumber == "Q25").Id);
        Assert.Equal("q36", questions.Single(question => question.DisplayNumber == "Q26").Id);
        Assert.Equal("q23", questions.Single(question => question.DisplayNumber == "Q27").Id);
    }

    [Fact]
    public void V3_Preserves_All_V2_Question_Definitions_And_Section_Assignments()
    {
        foreach (var v2Section in QuestionnaireV2.Definition.Sections)
        {
            var v3Section = Assert.Single(QuestionnaireV3.Definition.Sections.Where(section => section.Id == v2Section.Id));

            foreach (var v2Question in v2Section.Questions)
            {
                var v3Question = Assert.Single(v3Section.Questions.Where(question => question.Id == v2Question.Id));
                AssertQuestionDefinitionsEqual(v2Question, v3Question);
            }
        }
    }

    [Fact]
    public void V3_New_Questions_Have_Exact_Required_Choices()
    {
        var questions = QuestionnaireV3.Definition.Sections.SelectMany(section => section.Questions).ToDictionary(question => question.Id);

        AssertNewQuestion(questions["q33"], "For the equipment and systems relevant to your priority operational challenge, how much of the needed data is available through automatic connections, without routine manual re-entry or file transfers?", new[]
        {
            ("none", "None", false),
            ("less_than_quarter", "Less than one-quarter", false),
            ("about_quarter_to_half", "About one-quarter to one-half", false),
            ("most", "More than one-half, but not all or nearly all", false),
            ("nearly_all", "All or nearly all", false),
            ("unknown", "Unknown / Not sure", true)
        });
        AssertNewQuestion(questions["q34"], "For those automatic connections, how reliably does data arrive in time for the operational work that depends on it?", new[]
        {
            ("not_available", "No automatic data exchange is available", false),
            ("often_late_or_incomplete", "Often late, incomplete, or unavailable", false),
            ("sometimes_late_or_incomplete", "Sometimes late or incomplete", false),
            ("usually_on_time", "Usually arrives complete and in time", false),
            ("consistently_reliable", "Consistently complete and timely, with issues identified and addressed", false),
            ("unknown", "Unknown / Not sure", true)
        });
        AssertNewQuestion(questions["q35"], "How are important operational data values checked for accuracy, and how are identified issues handled?", new[]
        {
            ("no_checks", "Values are not routinely checked", false),
            ("informal_checks", "Issues are usually noticed informally or after they affect work", false),
            ("some_checks", "Some important values are checked, but coverage or follow-up is inconsistent", false),
            ("routine_checks", "Important values are routinely checked and issues are followed up", false),
            ("systematic_monitoring", "Checks cover important data consistently; issues are tracked and addressed", false),
            ("unknown", "Unknown / Not sure", true)
        });
        AssertNewQuestion(questions["q36"], "For the operational data needed to investigate your priority challenge, how often are required readings or records missing?", new[]
        {
            ("frequently_missing", "Missing frequently, with important gaps", false),
            ("often_missing", "Missing often", false),
            ("sometimes_missing", "Missing sometimes; impact varies", false),
            ("rarely_missing", "Rarely missing; gaps are usually identified", false),
            ("almost_never_missing", "Almost never missing; completeness is monitored", false),
            ("unknown", "Unknown / Not sure", true)
        });
    }

    [Fact]
    public void Resolve_For_3_0_Returns_QuestionnaireV3_Definition()
    {
        Assert.Same(QuestionnaireV3.Definition, QuestionnaireDefinitionResolver.Resolve("3.0"));
    }

    [Fact]
    public void Resolve_For_2_0_Returns_QuestionnaireV2_Definition()
    {
        Assert.Same(QuestionnaireV2.Definition, QuestionnaireDefinitionResolver.Resolve("2.0"));
    }

    [Theory]
    [InlineData("4.0")]
    [InlineData("1.1")]
    [InlineData("v1")]
    [InlineData("1")]
    public void Resolve_Rejects_Unknown_Versions(string version)
    {
        var ex = Assert.Throws<ArgumentException>(() => QuestionnaireDefinitionResolver.Resolve(version));
        Assert.Contains(version, ex.Message);
    }

    [Fact]
    public void Resolve_Rejects_Null_Version()
    {
        Assert.Throws<ArgumentException>(() => QuestionnaireDefinitionResolver.Resolve(null));
    }

    [Fact]
    public void Resolve_Rejects_Empty_Version()
    {
        Assert.Throws<ArgumentException>(() => QuestionnaireDefinitionResolver.Resolve(string.Empty));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Resolve_Rejects_Whitespace_Version(string version)
    {
        Assert.Throws<ArgumentException>(() => QuestionnaireDefinitionResolver.Resolve(version));
    }

    [Fact]
    public void Resolve_Is_Deterministic_Across_Multiple_Calls()
    {
        var first = QuestionnaireDefinitionResolver.Resolve("1.0");
        var second = QuestionnaireDefinitionResolver.Resolve("1.0");

        Assert.Same(first, second);
        Assert.Same(QuestionnaireV1.Definition, first);
    }

    private static void AssertQuestionDefinitionsEqual(QuestionDefinition expected, QuestionDefinition actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.IsOptional, actual.IsOptional);
        Assert.Equal(expected.VisibilityCondition, actual.VisibilityCondition);
        Assert.Equal(expected.MinSelections, actual.MinSelections);
        Assert.Equal(expected.MaxSelections, actual.MaxSelections);
        Assert.Equal(expected.MaxOccurrences, actual.MaxOccurrences);
        Assert.Equal(expected.RequiresDiscovery, actual.RequiresDiscovery);
        Assert.Equal(expected.Choices?.Select(ChoiceTuple).ToArray(), actual.Choices?.Select(ChoiceTuple).ToArray());
        Assert.Equal(expected.SupplementalFields?.Select(SupplementalTuple).ToArray(), actual.SupplementalFields?.Select(SupplementalTuple).ToArray());

        if (expected.RepeatableFields is null)
        {
            Assert.Null(actual.RepeatableFields);
            return;
        }

        Assert.NotNull(actual.RepeatableFields);
        Assert.Equal(expected.RepeatableFields.Length, actual.RepeatableFields!.Length);
        for (var index = 0; index < expected.RepeatableFields.Length; index++)
        {
            var expectedField = expected.RepeatableFields[index];
            var actualField = actual.RepeatableFields[index];
            Assert.Equal(expectedField.Id, actualField.Id);
            Assert.Equal(expectedField.Label, actualField.Label);
            Assert.Equal(expectedField.Type, actualField.Type);
            Assert.Equal(expectedField.IsOptional, actualField.IsOptional);
            Assert.Equal(expectedField.ChoicesArray?.Select(ChoiceTuple).ToArray(), actualField.ChoicesArray?.Select(ChoiceTuple).ToArray());
        }
    }

    private static (string Value, string Label, bool IsUnknown, bool RequiresDiscovery) ChoiceTuple(Choice choice)
        => (choice.Value, choice.Label, choice.IsUnknown, choice.RequiresDiscovery);

    private static (string Id, string Label, bool IsOptional, string? TriggerChoiceValue) SupplementalTuple(SupplementalFieldDefinition field)
        => (field.Id, field.Label, field.IsOptional, field.TriggerChoiceValue);

    private static void AssertChoices(QuestionDefinition question, (string Value, string Label, bool IsUnknown)[] expected)
    {
        Assert.NotNull(question.Choices);
        Assert.Equal(expected, question.Choices!.Select(choice => (choice.Value, choice.Label, choice.IsUnknown)));
    }

    private static void AssertNewQuestion(QuestionDefinition question, string text, (string Value, string Label, bool IsUnknown)[] choices)
    {
        Assert.Equal(text, question.Text);
        Assert.Equal(QuestionType.SingleSelect, question.Type);
        Assert.False(question.IsOptional);
        AssertChoices(question, choices);
    }
}
