using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

namespace SentinelMfgSuite.Web.Tests
{
    public class QuestionnaireResponseTests
    {
        [Fact]
        public void SchemaVersion_RoundTrips()
        {
            var resp = new QuestionnaireResponseV1();
            resp.SchemaVersion = "1.0";

            var json = QuestionnaireResponseV1Serializer.Serialize(resp);
            var outResp = QuestionnaireResponseV1Serializer.Deserialize(json);

            Assert.NotNull(outResp);
            Assert.Equal("1.0", outResp!.SchemaVersion);
        }

        [Fact]
        public void V2_Response_RoundTrips_Schema_And_New_Answers()
        {
            var response = CreateV2Response();

            var json = QuestionnaireResponseV1Serializer.Serialize(response);
            var roundTrip = QuestionnaireResponseV1Serializer.Deserialize(json);

            Assert.NotNull(roundTrip);
            Assert.Equal("2.0", roundTrip!.SchemaVersion);
            Assert.Equal("generally_accurate", roundTrip.Answers["q31"].Selected);
            Assert.Equal("investigate_improve", roundTrip.Answers["q32"].Selected);
            Assert.Equal("North Plant", roundTrip.Answers["q01"].Composite!["facility_name"].Text);
            Assert.Equal("11-25", roundTrip.Answers["q04"].Selected);
        }

        [Fact]
        public void V3_Response_RoundTrips_New_Question_Answer_Keys_And_Values()
        {
            var response = CreateV3Response();

            var serialized = QuestionnaireResponseV1Serializer.Serialize(response);
            var roundTrip = QuestionnaireResponseV1Serializer.Deserialize(serialized);

            Assert.NotNull(roundTrip);
            Assert.Equal("3.0", roundTrip!.SchemaVersion);
            Assert.Equal("nearly_all", roundTrip.Answers["q33"].Selected);
            Assert.Equal("usually_on_time", roundTrip.Answers["q34"].Selected);
            Assert.Equal("routine_checks", roundTrip.Answers["q35"].Selected);
            Assert.Equal("rarely_missing", roundTrip.Answers["q36"].Selected);
            Assert.Contains("\"q33\"", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Q17\"", serialized, StringComparison.Ordinal);
        }

        [Fact]
        public void V3_Validation_Accepts_Required_Questions_With_Explicit_Unknown()
        {
            var response = CreateV3Response();
            response.Answers["q33"].Selected = "unknown";
            response.Answers["q34"].Selected = "unknown";
            response.Answers["q35"].Selected = "unknown";
            response.Answers["q36"].Selected = "unknown";

            var result = QuestionnaireResponseV1Validator.Validate(response, QuestionnaireV3.Definition, QuestionnaireValidationMode.Submission);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }

        [Theory]
        [InlineData("q33")]
        [InlineData("q34")]
        [InlineData("q35")]
        [InlineData("q36")]
        public void V3_Submission_Requires_Each_New_Question(string questionId)
        {
            var response = CreateV3Response();
            response.Answers.Remove(questionId);

            var result = QuestionnaireResponseV1Validator.Validate(response, QuestionnaireV3.Definition, QuestionnaireValidationMode.Submission);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.QuestionId == questionId && error.Message == "Question is required for submission.");
        }

        [Theory]
        [InlineData("1.0", "q01", "Q01")]
        [InlineData("2.0", "q01", "Q01")]
        [InlineData("3.0", "q33", "Q17")]
        [InlineData("3.0", "q34", "Q18")]
        [InlineData("3.0", "q35", "Q25")]
        [InlineData("3.0", "q36", "Q26")]
        public void Submission_Required_Question_Error_Preserves_Id_And_Resolves_Display_Number(
            string schemaVersion,
            string questionId,
            string displayNumber)
        {
            var definition = QuestionnaireDefinitionResolver.Resolve(schemaVersion);
            var response = new QuestionnaireResponseV1 { SchemaVersion = schemaVersion };

            var result = QuestionnaireResponseV1Validator.Validate(response, definition, QuestionnaireValidationMode.Submission);
            var error = Assert.Single(result.Errors.Where(error => error.QuestionId == questionId));
            var question = Assert.Single(definition.Sections.SelectMany(section => section.Questions).Where(question => question.Id == error.QuestionId));

            Assert.Equal("Question is required for submission.", error.Message);
            Assert.Equal(questionId, error.QuestionId);
            Assert.Equal(displayNumber, question.DisplayNumber);
        }

        [Theory]
        [InlineData("q33", "not-a-coverage-value")]
        [InlineData("q34", "not-a-reliability-value")]
        [InlineData("q35", "not-a-validation-value")]
        [InlineData("q36", "not-a-completeness-value")]
        public void V3_Validation_Rejects_Unsupported_New_Question_Values(string questionId, string value)
        {
            var response = CreateV3Response();
            response.Answers[questionId].Selected = value;

            var result = QuestionnaireResponseV1Validator.Validate(response, QuestionnaireV3.Definition, QuestionnaireValidationMode.Draft);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.QuestionId == questionId && error.Message.Contains("not a supported choice value", StringComparison.Ordinal));
        }

        [Fact]
        public void V2_Validation_Accepts_New_And_Inherited_Answers()
        {
            var result = QuestionnaireResponseV1Validator.Validate(
                CreateV2Response(),
                QuestionnaireV2.Definition,
                QuestionnaireValidationMode.Draft);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }

        [Theory]
        [InlineData("q31", "invalid_reliability")]
        [InlineData("q32", "invalid_decision_use")]
        public void V2_Validation_Rejects_Invalid_New_Question_Choices(string questionId, string choice)
        {
            var response = CreateV2Response();
            response.Answers[questionId].Selected = choice;

            var result = QuestionnaireResponseV1Validator.Validate(
                response,
                QuestionnaireV2.Definition,
                QuestionnaireValidationMode.Draft);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.QuestionId == questionId && error.Message.Contains("not a supported choice value", StringComparison.Ordinal));
        }

        [Fact]
        public void V2_Validation_Rejects_Unknown_Question_Id_Using_Existing_Rule()
        {
            var response = CreateV2Response();
            response.Answers["q99"] = new QuestionAnswer { Text = "Unexpected" };

            var result = QuestionnaireResponseV1Validator.Validate(
                response,
                QuestionnaireV2.Definition,
                QuestionnaireValidationMode.Draft);

            Assert.Contains(result.Errors, error => error.QuestionId == "q99" && error.Message == "Unknown question ID.");
        }

        [Fact]
        public void V2_Response_Validated_Against_V1_Is_Rejected()
        {
            var result = QuestionnaireResponseV1Validator.Validate(
                CreateV2Response(),
                QuestionnaireV1.Definition,
                QuestionnaireValidationMode.Draft);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Message.Contains("Schema version must be '1.0'", StringComparison.Ordinal));
            Assert.Contains(result.Errors, error => error.QuestionId == "q31" && error.Message == "Unknown question ID.");
            Assert.Contains(result.Errors, error => error.QuestionId == "q32" && error.Message == "Unknown question ID.");
        }

        [Fact]
        public void V1_Response_Validated_Against_V2_Is_Rejected()
        {
            var response = new QuestionnaireResponseV1
            {
                SchemaVersion = "1.0",
                Answers = new Dictionary<string, QuestionAnswer>
                {
                    ["q04"] = new QuestionAnswer { Selected = "11-25" }
                }
            };

            var result = QuestionnaireResponseV1Validator.Validate(
                response,
                QuestionnaireV2.Definition,
                QuestionnaireValidationMode.Draft);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Message.Contains("Schema version must be '2.0'", StringComparison.Ordinal));
        }

        [Fact]
        public void Historical_V1_Response_RoundTrip_Remains_Unchanged()
        {
            const string historicalJson = "{\"schemaVersion\":\"1.0\",\"answers\":{\"q04\":{\"selected\":\"11-25\"}}}";

            var response = QuestionnaireResponseV1Serializer.Deserialize(historicalJson);
            var serialized = QuestionnaireResponseV1Serializer.Serialize(response!);
            var roundTrip = QuestionnaireResponseV1Serializer.Deserialize(serialized);

            Assert.NotNull(roundTrip);
            Assert.Equal("1.0", roundTrip!.SchemaVersion);
            Assert.Single(roundTrip.Answers);
            Assert.Equal("11-25", roundTrip.Answers["q04"].Selected);
            Assert.DoesNotContain("q31", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("q32", serialized, StringComparison.Ordinal);
        }

        [Fact]
        public void QuestionnaireDefinition_Serializes_QuestionType_As_String_Names()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            options.Converters.Add(new JsonStringEnumConverter());

            var json = JsonSerializer.Serialize(QuestionnaireV1.Definition, options);

            Assert.Contains("\"type\":\"Composite\"", json);
            Assert.DoesNotContain("\"type\":2", json);
        }

        [Fact]
        public void TextAnswer_RoundTrips()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q02"] = new QuestionAnswer { Text = "Widgets" };

            var round = QuestionnaireResponseV1Serializer.Deserialize(QuestionnaireResponseV1Serializer.Serialize(resp));
            Assert.NotNull(round);
            Assert.True(round!.Answers.ContainsKey("q02"));
            Assert.Equal("Widgets", round.Answers["q02"].Text);
        }

        [Fact]
        public void SingleSelect_RoundTrips()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q04"] = new QuestionAnswer { Selected = "11-25" };

            var round = QuestionnaireResponseV1Serializer.Deserialize(QuestionnaireResponseV1Serializer.Serialize(resp));
            Assert.NotNull(round);
            Assert.Equal("11-25", round!.Answers["q04"].Selected);
        }

        [Fact]
        public void MultiSelect_PreservesAllValues()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q11"] = new QuestionAnswer { SelectedValues = new List<string>{ "plc", "mes" } };

            var round = QuestionnaireResponseV1Serializer.Deserialize(QuestionnaireResponseV1Serializer.Serialize(resp));
            Assert.NotNull(round);
            var vals = round!.Answers["q11"].SelectedValues;
            Assert.NotNull(vals);
            Assert.Contains("plc", vals);
            Assert.Contains("mes", vals);
            Assert.Equal(2, vals.Count);
        }

        [Fact]
        public void Q01_Composite_PreservesFields()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q01"] = new QuestionAnswer {
                Composite = new Dictionary<string, FieldAnswer>
                {
                    ["facility_name"] = new FieldAnswer{ Text = "North Plant" },
                    ["facility_location"] = new FieldAnswer{ Text = "Building A" }
                }
            };

            var round = QuestionnaireResponseV1Serializer.Deserialize(QuestionnaireResponseV1Serializer.Serialize(resp));
            Assert.NotNull(round);
            Assert.True(round!.Answers.ContainsKey("q01"));
            var comp = round.Answers["q01"].Composite;
            Assert.NotNull(comp);
            Assert.Equal("North Plant", comp!["facility_name"].Text);
            Assert.Equal("Building A", comp["facility_location"].Text);
        }

        [Fact]
        public void Q13_Repeatable_PreservesRecords()
        {
            var resp = new QuestionnaireResponseV1();
            var rec1 = new Dictionary<string, FieldAnswer>
            {
                ["source_name"] = new FieldAnswer{ Text = "PLC Line 1" },
                ["system_type"] = new FieldAnswer{ Selected = "plc" },
                ["vendor_platform"] = new FieldAnswer{ Text = "Rockwell" }
            };
            var rec2 = new Dictionary<string, FieldAnswer>
            {
                ["source_name"] = new FieldAnswer{ Text = "Historian" },
                ["system_type"] = new FieldAnswer{ Selected = "historian" }
                // vendor_platform intentionally omitted to test optional missing fields
            };

            resp.Answers["q13"] = new QuestionAnswer { Repeatable = new List<Dictionary<string, FieldAnswer>>{ rec1, rec2 } };

            var round = QuestionnaireResponseV1Serializer.Deserialize(QuestionnaireResponseV1Serializer.Serialize(resp));
            Assert.NotNull(round);
            var list = round!.Answers["q13"].Repeatable;
            Assert.NotNull(list);
            Assert.Equal(2, list!.Count);
            Assert.Equal("PLC Line 1", list[0]["source_name"].Text);
            Assert.Equal("Rockwell", list[0]["vendor_platform"].Text);
            Assert.Equal("Historian", list[1]["source_name"].Text);
            Assert.False(list[1].ContainsKey("vendor_platform"));
        }

        [Fact]
        public void Unknown_Remains_Distinguishable_From_Missing()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q12"] = new QuestionAnswer { SelectedValues = new List<string>{ "unknown" } };

            var round = QuestionnaireResponseV1Serializer.Deserialize(QuestionnaireResponseV1Serializer.Serialize(resp));
            Assert.NotNull(round);
            Assert.True(round!.Answers.ContainsKey("q12"));
            Assert.Contains("unknown", round.Answers["q12"].SelectedValues!);

            // q14 not present: missing vs explicit Unknown
            Assert.False(round.Answers.ContainsKey("q14"));
        }

        [Fact]
        public void IncompleteDraft_RoundTrips_Without_Creating_MissingAnswers()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q02"] = new QuestionAnswer{ Text = "Widgets" };
            resp.Answers["q11"] = new QuestionAnswer{ SelectedValues = new List<string>{ "plc" } };

            var round = QuestionnaireResponseV1Serializer.Deserialize(QuestionnaireResponseV1Serializer.Serialize(resp));
            Assert.NotNull(round);
            Assert.Equal(2, round!.Answers.Count);
            Assert.True(round.Answers.ContainsKey("q02"));
            Assert.True(round.Answers.ContainsKey("q11"));
            Assert.False(round.Answers.ContainsKey("q03"));
        }

        [Fact]
        public void Q27_Composite_Preserves_DesiredOutcome_And_Baseline()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q27"] = new QuestionAnswer {
                Composite = new Dictionary<string, FieldAnswer>
                {
                    ["desired_outcome"] = new FieldAnswer{ Text = "Reduce downtime by 20%" },
                    ["baseline_available"] = new FieldAnswer{ Selected = "partial" }
                }
            };

            var round = QuestionnaireResponseV1Serializer.Deserialize(QuestionnaireResponseV1Serializer.Serialize(resp));
            Assert.NotNull(round);
            var comp = round!.Answers["q27"].Composite;
            Assert.NotNull(comp);
            Assert.Equal("Reduce downtime by 20%", comp!["desired_outcome"].Text);
            Assert.Equal("partial", comp["baseline_available"].Selected);
        }

        [Fact]
        public void DraftValidation_Allows_Incomplete_Response_And_Record()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q02"] = new QuestionAnswer { Text = "Widgets" };
            resp.Answers["q13"] = new QuestionAnswer
            {
                Repeatable = new List<Dictionary<string, FieldAnswer>>
                {
                    new Dictionary<string, FieldAnswer>
                    {
                        ["source_name"] = new FieldAnswer { Text = "Line 1 PLC" }
                    }
                }
            };

            var result = QuestionnaireResponseV1Validator.Validate(resp, QuestionnaireValidationMode.Draft);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void DraftValidation_Rejects_Unknown_Question_Id_And_Invalid_Answer_Shape()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q99"] = new QuestionAnswer { Text = "Unexpected" };
            resp.Answers["q04"] = new QuestionAnswer { Text = "wrong-shape" };

            var result = QuestionnaireResponseV1Validator.Validate(resp, QuestionnaireValidationMode.Draft);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.QuestionId == "q99" && error.Message.Contains("Unknown question ID", StringComparison.Ordinal));
            Assert.Contains(result.Errors, error => error.QuestionId == "q04" && error.Message.Contains("Answer shape is invalid", StringComparison.Ordinal));
        }

        [Fact]
        public void DraftValidation_Rejects_Invalid_Choices_And_Exclusive_MultiSelect_Values()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q04"] = new QuestionAnswer { Selected = "bogus" };
            resp.Answers["q14"] = new QuestionAnswer { SelectedValues = new List<string> { "not_collected", "manual_entry" } };

            var result = QuestionnaireResponseV1Validator.Validate(resp, QuestionnaireValidationMode.Draft);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.QuestionId == "q04" && error.Message.Contains("not a supported choice value", StringComparison.Ordinal));
            Assert.Contains(result.Errors, error => error.QuestionId == "q14" && error.Message.Contains("Exclusive choice values cannot be combined", StringComparison.Ordinal));
        }

        [Fact]
        public void SubmissionValidation_Rejects_Missing_Required_Questions_And_Fields()
        {
            var resp = CreateValidSubmissionResponse();
            resp.Answers.Remove("q05");
            resp.Answers["q01"] = new QuestionAnswer
            {
                Composite = new Dictionary<string, FieldAnswer>
                {
                    ["facility_name"] = new FieldAnswer { Text = "North Plant" }
                }
            };

            var result = QuestionnaireResponseV1Validator.Validate(resp, QuestionnaireValidationMode.Submission);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.QuestionId == "q05" && error.Message.Contains("required for submission", StringComparison.Ordinal));
            Assert.Contains(result.Errors, error => error.QuestionId == "q01" && error.FieldId == "facility_location" && error.Message.Contains("required", StringComparison.Ordinal));
        }

        [Fact]
        public void SubmissionValidation_Rejects_Q06_Above_Maximum_And_Q07_Outside_Selected_Challenges()
        {
            var resp = CreateValidSubmissionResponse();
            resp.Answers["q06"] = new QuestionAnswer
            {
                SelectedValues = new List<string> { "unplanned_downtime", "quality", "throughput", "energy" }
            };
            resp.Answers["q07"] = new QuestionAnswer { Selected = "manual_data" };

            var result = QuestionnaireResponseV1Validator.Validate(resp, QuestionnaireValidationMode.Submission);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.QuestionId == "q06" && error.Message.Contains("No more than 3 selections", StringComparison.Ordinal));
            Assert.Contains(result.Errors, error => error.QuestionId == "q07" && error.Message.Contains("must match one of the selected q06 challenges", StringComparison.Ordinal));
        }

        [Fact]
        public void SubmissionValidation_Rejects_Q13_TooMany_Records_And_Missing_Required_Record_Fields()
        {
            var resp = CreateValidSubmissionResponse();
            resp.Answers["q13"] = new QuestionAnswer
            {
                Repeatable = new List<Dictionary<string, FieldAnswer>>
                {
                    new Dictionary<string, FieldAnswer>
                    {
                        ["vendor_platform"] = new FieldAnswer { Text = "Unknown" }
                    },
                    CreateValidDataSourceRecord("Source 2", "plc"),
                    CreateValidDataSourceRecord("Source 3", "scada"),
                    CreateValidDataSourceRecord("Source 4", "historian"),
                    CreateValidDataSourceRecord("Source 5", "mes"),
                    CreateValidDataSourceRecord("Source 6", "erp")
                }
            };

            var result = QuestionnaireResponseV1Validator.Validate(resp, QuestionnaireValidationMode.Submission);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.QuestionId == "q13" && error.Message.Contains("No more than 5 records", StringComparison.Ordinal));
            Assert.Contains(result.Errors, error => error.QuestionId == "q13" && error.FieldId == "source_name" && error.Message.Contains("Record 1", StringComparison.Ordinal));
            Assert.Contains(result.Errors, error => error.QuestionId == "q13" && error.FieldId == "system_type" && error.Message.Contains("Record 1", StringComparison.Ordinal));
        }

        [Fact]
        public void SubmissionValidation_Rejects_Invalid_Composite_Field_Shape()
        {
            var resp = CreateValidSubmissionResponse();
            resp.Answers["q27"] = new QuestionAnswer
            {
                Composite = new Dictionary<string, FieldAnswer>
                {
                    ["desired_outcome"] = new FieldAnswer { Selected = "invalid" },
                    ["baseline_available"] = new FieldAnswer { Selected = "partial" }
                }
            };

            var result = QuestionnaireResponseV1Validator.Validate(resp, QuestionnaireValidationMode.Submission);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.QuestionId == "q27" && error.FieldId == "desired_outcome" && error.Message.Contains("text field", StringComparison.Ordinal));
        }

        [Fact]
        public void SubmissionValidation_Accepts_Valid_Response()
        {
            var resp = CreateValidSubmissionResponse();
            resp.Answers["q13"] = new QuestionAnswer
            {
                Repeatable = new List<Dictionary<string, FieldAnswer>>
                {
                    new Dictionary<string, FieldAnswer>
                    {
                        ["source_name"] = new FieldAnswer { Text = "Line 1 PLC" },
                        ["system_type"] = new FieldAnswer { Selected = "plc" },
                        ["vendor_platform"] = new FieldAnswer { Text = string.Empty },
                        ["connection_method"] = new FieldAnswer { Selected = "unknown" },
                        ["access_status"] = new FieldAnswer { Selected = "available" }
                    }
                }
            };

            var result = QuestionnaireResponseV1Validator.Validate(resp, QuestionnaireValidationMode.Submission);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void Supplemental_RoundTrips_For_SingleSelect_And_MultiSelect()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q24"] = new QuestionAnswer
            {
                Selected = "manual",
                Supplemental = new Dictionary<string, string?>
                {
                    ["kpi_notes"] = "Calculated in spreadsheets"
                }
            };
            resp.Answers["q22"] = new QuestionAnswer
            {
                SelectedValues = new List<string> { "other" },
                Supplemental = new Dictionary<string, string?>
                {
                    ["quality_notes"] = "Intermittent gaps overnight",
                    ["other"] = "Missing shift handoff entries"
                }
            };

            var round = QuestionnaireResponseV1Serializer.Deserialize(QuestionnaireResponseV1Serializer.Serialize(resp));

            Assert.NotNull(round);
            Assert.Equal("Calculated in spreadsheets", round!.Answers["q24"].Supplemental!["kpi_notes"]);
            Assert.Equal("Intermittent gaps overnight", round.Answers["q22"].Supplemental!["quality_notes"]);
            Assert.Equal("Missing shift handoff entries", round.Answers["q22"].Supplemental!["other"]);
        }

        [Fact]
        public void DraftValidation_Allows_Blank_Optional_Supplemental_Notes()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q24"] = new QuestionAnswer
            {
                Selected = "manual",
                Supplemental = new Dictionary<string, string?>
                {
                    ["kpi_notes"] = string.Empty
                }
            };
            resp.Answers["q29"] = new QuestionAnswer
            {
                SelectedValues = new List<string> { "budget" },
                Supplemental = new Dictionary<string, string?>
                {
                    ["constraint_notes"] = string.Empty
                }
            };

            var result = QuestionnaireResponseV1Validator.Validate(resp, QuestionnaireValidationMode.Draft);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void DraftValidation_Rejects_Unsupported_Supplemental_Field_Id()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q24"] = new QuestionAnswer
            {
                Selected = "manual",
                Supplemental = new Dictionary<string, string?>
                {
                    ["other"] = "Not supported here"
                }
            };

            var result = QuestionnaireResponseV1Validator.Validate(resp, QuestionnaireValidationMode.Draft);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.QuestionId == "q24" && error.FieldId == "other" && error.Message.Contains("Unsupported supplemental field ID", StringComparison.Ordinal));
        }

        [Fact]
        public void DraftValidation_Rejects_Other_Supplemental_When_Other_Is_Not_Selected()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q03"] = new QuestionAnswer
            {
                SelectedValues = new List<string> { "discrete" },
                Supplemental = new Dictionary<string, string?>
                {
                    ["other"] = "Custom manual workflow"
                }
            };

            var result = QuestionnaireResponseV1Validator.Validate(resp, QuestionnaireValidationMode.Draft);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.QuestionId == "q03" && error.FieldId == "other" && error.Message.Contains("only allowed when 'other' is selected", StringComparison.Ordinal));
        }

        [Fact]
        public void DraftValidation_Accepts_Other_Supplemental_When_Other_Is_Selected()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q03"] = new QuestionAnswer
            {
                SelectedValues = new List<string> { "other" },
                Supplemental = new Dictionary<string, string?>
                {
                    ["other"] = "Cell-based assembly"
                }
            };

            var result = QuestionnaireResponseV1Validator.Validate(resp, QuestionnaireValidationMode.Draft);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }

        private static QuestionnaireResponseV1 CreateValidSubmissionResponse()
        {
            var resp = new QuestionnaireResponseV1();
            resp.Answers["q01"] = new QuestionAnswer
            {
                Composite = new Dictionary<string, FieldAnswer>
                {
                    ["facility_name"] = new FieldAnswer { Text = "North Plant" },
                    ["facility_location"] = new FieldAnswer { Text = "Building A" }
                }
            };
            resp.Answers["q04"] = new QuestionAnswer { Selected = "11-25" };
            resp.Answers["q05"] = new QuestionAnswer { Selected = "two_shifts" };
            resp.Answers["q06"] = new QuestionAnswer { SelectedValues = new List<string> { "unplanned_downtime", "quality" } };
            resp.Answers["q07"] = new QuestionAnswer { Selected = "quality" };
            resp.Answers["q11"] = new QuestionAnswer { SelectedValues = new List<string> { "plc", "mes" } };
            resp.Answers["q14"] = new QuestionAnswer { SelectedValues = new List<string> { "auto_equipment", "db_queries" } };
            resp.Answers["q27"] = new QuestionAnswer
            {
                Composite = new Dictionary<string, FieldAnswer>
                {
                    ["desired_outcome"] = new FieldAnswer { Text = "Reduce downtime by 20%" },
                    ["baseline_available"] = new FieldAnswer { Selected = "partial" }
                }
            };
            return resp;
        }

        private static QuestionnaireResponseV1 CreateV2Response()
        {
            var response = CreateValidSubmissionResponse();
            response.SchemaVersion = "2.0";
            response.Answers["q31"] = new QuestionAnswer { Selected = "generally_accurate" };
            response.Answers["q32"] = new QuestionAnswer { Selected = "investigate_improve" };
            return response;
        }

        private static QuestionnaireResponseV1 CreateV3Response()
        {
            var response = CreateV2Response();
            response.SchemaVersion = "3.0";
            response.Answers["q33"] = new QuestionAnswer { Selected = "nearly_all" };
            response.Answers["q34"] = new QuestionAnswer { Selected = "usually_on_time" };
            response.Answers["q35"] = new QuestionAnswer { Selected = "routine_checks" };
            response.Answers["q36"] = new QuestionAnswer { Selected = "rarely_missing" };
            return response;
        }

        private static Dictionary<string, FieldAnswer> CreateValidDataSourceRecord(string sourceName, string systemType)
        {
            return new Dictionary<string, FieldAnswer>
            {
                ["source_name"] = new FieldAnswer { Text = sourceName },
                ["system_type"] = new FieldAnswer { Selected = systemType },
                ["vendor_platform"] = new FieldAnswer { Text = "Vendor" }
            };
        }
    }
}
