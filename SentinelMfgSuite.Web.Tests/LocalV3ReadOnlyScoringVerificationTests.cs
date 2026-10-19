using System.Globalization;
using Npgsql;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;
using Xunit;
using Xunit.Abstractions;

namespace SentinelMfgSuite.Web.Tests;

public sealed class LocalV3ReadOnlyScoringVerificationTests
{
    private const string RunEnvironmentVariable = "SENTINEL_RUN_LOCAL_V3_SCORING_VERIFICATION";
    private const string ConnectionEnvironmentVariable = "SENTINEL_READONLY_SCORING_CONNECTION";
    private static readonly Guid TargetAssessmentId = Guid.Parse("d530f984-81b3-433c-8a81-849401cd17ae");

    private readonly ITestOutputHelper _output;

    public LocalV3ReadOnlyScoringVerificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [LocalOnlyFact]
    [Trait("Category", "LocalReadOnlyScoringVerification")]
    public async Task Submitted_V3_Assessment_Can_Be_Scored_ReadOnly()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(RunEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            _output.WriteLine($"Skipped: set {RunEnvironmentVariable}=1 to explicitly enable local verification.");
            return;
        }

        var rawConnection = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(rawConnection))
        {
            throw new InvalidOperationException($"Required dedicated connection setting {ConnectionEnvironmentVariable} is missing.");
        }

        var connectionString = ValidateAndNormalizeConnectionString(rawConnection);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await using (var readOnlyCommand = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnlyCommand.ExecuteNonQueryAsync();
        }

        const string assessmentSql = "SELECT \"Title\", \"Status\", \"QuestionnaireVersion\" FROM \"Assessment\" WHERE \"Id\" = @assessment_id";
        string title;
        short status;
        string questionnaireVersion;
        await using (var command = new NpgsqlCommand(assessmentSql, connection, transaction))
        {
            command.Parameters.AddWithValue("assessment_id", TargetAssessmentId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "Expected assessment was not found.");
            title = reader.GetString(0);
            status = reader.GetInt16(1);
            questionnaireVersion = reader.GetString(2);
            Assert.False(await reader.ReadAsync(), "Assessment identifier returned multiple rows.");
        }

        Assert.Equal("Sentinel V3 UAT", title);
        Assert.Equal((short)3, status);
        Assert.Equal("3.0", questionnaireVersion);

        const string responseSql = "SELECT \"Data\"::text, \"SubmittedUtc\" FROM \"AssessmentResponse\" WHERE \"AssessmentId\" = @assessment_id AND \"IsDraft\" = FALSE AND \"SubmittedUtc\" IS NOT NULL ORDER BY \"SubmittedUtc\" DESC, \"LastSavedUtc\" DESC, \"Id\" DESC LIMIT 1";
        string responseJson;
        await using (var command = new NpgsqlCommand(responseSql, connection, transaction))
        {
            command.Parameters.AddWithValue("assessment_id", TargetAssessmentId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "No submitted response was found for the assessment.");
            responseJson = reader.GetString(0);
            Assert.False(reader.IsDBNull(1), "Latest response is not submitted.");
        }

        var response = QuestionnaireResponseV1Serializer.Deserialize(responseJson);
        Assert.NotNull(response);
        Assert.Equal("3.0", response!.SchemaVersion);

        var result = new ReadinessScoringService().Score("3.0", response);
        _output.WriteLine($"Assessment: title verified; status={nameof(AssessmentStatus.Submitted)}; questionnaireVersion={questionnaireVersion}; responseSchemaVersion={response.SchemaVersion}.");
        foreach (var dimension in result.Dimensions)
        {
            _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0} {1}: status={2}, rawScore={3}, displayScore={4}, classification={5}, scoreable/eligible={6}/{7}, evidenceCoverage={8}",
                dimension.DimensionId,
                dimension.Name,
                dimension.Status,
                FormatNumber(dimension.RawScore0To100),
                FormatScore(dimension.DisplayScore0To100),
                dimension.Classification?.ToString() ?? "Withheld",
                dimension.ScoreableItemCount,
                dimension.TotalEligibleItemCount,
                dimension.EvidenceCoverage is { } coverage ? coverage.ToString("P1", CultureInfo.InvariantCulture) : "n/a"));
            foreach (var reason in dimension.WithholdingReasons)
            {
                _output.WriteLine($"  Withholding: {reason}");
            }
        }

        if (result.OverallDisplayScore0To100.HasValue)
        {
            _output.WriteLine($"Overall: status={result.OverallStatus}, rawScore={FormatNumber(result.OverallRawScore0To100)}, displayScore={result.OverallDisplayScore0To100}, classification={result.OverallClassification}");
        }
        else
        {
            _output.WriteLine($"Overall: status={result.OverallStatus}, score=withheld, reasons={string.Join(", ", result.WithholdingReasons)}");
        }

        foreach (var item in result.Dimensions.SelectMany(dimension => dimension.Items)
                     .Where(item => item.Status is ReadinessItemStatus.Missing or ReadinessItemStatus.Unknown
                         or ReadinessItemStatus.ReliabilityNotEstablished or ReadinessItemStatus.UnmappedValue
                         or ReadinessItemStatus.InvalidAnswerShape))
        {
            _output.WriteLine($"Evidence: item={item.ItemKey}, status={item.Status}, reason={item.Reason ?? item.Status.ToString()}");
        }

        foreach (var flag in result.ReviewFlags)
        {
            _output.WriteLine($"Consultant-review flag: code={flag.Code}, severity={flag.Severity}, questions={string.Join(",", flag.QuestionIds)}, message={flag.Message}");
        }

        if (result.ReviewFlags.Count == 0)
        {
            _output.WriteLine("Consultant-review flags: none.");
        }

        await transaction.RollbackAsync();
    }

    private static string ValidateAndNormalizeConnectionString(string rawConnection)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(rawConnection);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException("Dedicated local verification connection setting is invalid.", ex);
        }

        if (!string.Equals(builder.Host?.Trim(), "127.0.0.1", StringComparison.Ordinal)
            || builder.Port != 5432
            || !string.Equals(builder.Database?.Trim(), "sentinel_dev", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing connection: target must be exactly host 127.0.0.1, port 5432, database sentinel_dev.");
        }

        if (string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new InvalidOperationException("Refusing connection: dedicated local verification setting requires a username.");
        }

        builder.Pooling = false;
        builder.ApplicationName = "SentinelLocalReadOnlyScoringVerification";
        return builder.ConnectionString;
    }

    private static string FormatScore(int? score) => score?.ToString(CultureInfo.InvariantCulture) ?? "Withheld";

    private static string FormatNumber(double? score) => score?.ToString("F2", CultureInfo.InvariantCulture) ?? "Withheld";

    private sealed class LocalOnlyFactAttribute : FactAttribute
    {
        public LocalOnlyFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(RunEnvironmentVariable), "1", StringComparison.Ordinal))
            {
                Skip = $"Set {RunEnvironmentVariable}=1 to explicitly enable this local verification.";
            }
        }
    }

    private enum AssessmentStatus : short
    {
        Submitted = 3
    }
}
