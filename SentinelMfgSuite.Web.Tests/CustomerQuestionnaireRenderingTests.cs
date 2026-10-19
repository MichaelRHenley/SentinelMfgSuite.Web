using System.Net;
using System.Security.Cryptography;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class CustomerQuestionnaireRenderingTests
{
    [Fact]
    public async Task V2_Questionnaire_Get_Renders_32_Questions_And_All_Q31_Q32_Choices()
    {
        await using var factory = new CustomerQuestionnaireWebApplicationFactory();
        var seeded = await SeedAssessmentAsync(factory, "2.0");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/assessment/questionnaire/{seeded.InvitationId:D}/form?token={seeded.Token}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sections: 6 · Questions: 32", html, StringComparison.Ordinal);
        Assert.Contains("Complete 6 sections and 32 questions", html, StringComparison.Ordinal);
        Assert.Equal(6, Regex.Matches(html, "class=\\\"aq-section\\\"").Count);
        Assert.Equal(32, Regex.Matches(html, "class=\\\"[^\\\"]*\\baq-question\\b").Count);
        AssertQuestionNumbers(html, 32);
        AssertDisplayNumber(html, "q31", "Q27");
        AssertDisplayNumber(html, "q32", "Q28");
        AssertDisplayNumber(html, "q27", "Q29");
        AssertOrdered(html, "data-question-id=\"q26\"", "data-question-id=\"q31\"", "data-question-id=\"q32\"");

        AssertQuestionChoice(html, "q31", "consistently_accurate", "Consistently accurate and trusted");
        AssertQuestionChoice(html, "q31", "generally_accurate", "Generally accurate with occasional discrepancies");
        AssertQuestionChoice(html, "q31", "frequent_discrepancies", "Frequent discrepancies requiring manual reconciliation");
        AssertQuestionChoice(html, "q31", "reliability_not_established", "Reliability has not been established");
        AssertQuestionChoice(html, "q31", "unknown", "Unknown / Not sure");
        AssertQuestionChoice(html, "q32", "proactive_decisions", "Routinely used for proactive decisions");
        AssertQuestionChoice(html, "q32", "investigate_improve", "Regularly used to investigate and improve performance");
        AssertQuestionChoice(html, "q32", "retrospective_reporting", "Primarily used for retrospective reporting");
        AssertQuestionChoice(html, "q32", "limited_ad_hoc", "Limited or ad hoc use");
        AssertQuestionChoice(html, "q32", "not_used", "Not currently used");

        Assert.Contains("data-question-id=\"q04\"", html, StringComparison.Ordinal);
        Assert.Contains("1&#x2013;5", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"q31_consistently_accurate\" checked", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V2_Questionnaire_Get_Reopens_Draft_Answers_Without_Resetting_Schema_Version()
    {
        await using var factory = new CustomerQuestionnaireWebApplicationFactory();
        var seeded = await SeedAssessmentAsync(factory, "2.0", new QuestionnaireResponseV1
        {
            SchemaVersion = "2.0",
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q04"] = new() { Selected = "11-25" },
                ["q31"] = new() { Selected = "generally_accurate" },
                ["q32"] = new() { Selected = "investigate_improve" }
            }
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/assessment/questionnaire/{seeded.InvitationId:D}/form?token={seeded.Token}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var initialResponse = ReadInitialResponse(html);
        Assert.Equal("2.0", initialResponse.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("generally_accurate", initialResponse.RootElement.GetProperty("answers").GetProperty("q31").GetProperty("selected").GetString());
        Assert.Equal("investigate_improve", initialResponse.RootElement.GetProperty("answers").GetProperty("q32").GetProperty("selected").GetString());
        Assert.DoesNotContain("q27\":", initialResponse.RootElement.GetProperty("answers").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("value=\"generally_accurate\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"investigate_improve\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V1_Questionnaire_Get_Remains_At_30_Questions_Without_Q31_Q32()
    {
        await using var factory = new CustomerQuestionnaireWebApplicationFactory();
        var seeded = await SeedAssessmentAsync(factory, "1.0");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/assessment/questionnaire/{seeded.InvitationId:D}/form?token={seeded.Token}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sections: 6 · Questions: 30", html, StringComparison.Ordinal);
        Assert.Contains("Complete 6 sections and 30 questions", html, StringComparison.Ordinal);
        Assert.Equal(30, Regex.Matches(html, "class=\\\"[^\\\"]*\\baq-question\\b").Count);
        AssertQuestionNumbers(html, 30);
        Assert.DoesNotContain("data-question-id=\"q31\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-question-id=\"q32\"", html, StringComparison.Ordinal);
        Assert.Contains("data-question-id=\"q30\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V3_Questionnaire_Get_Renders_36_Questions_With_Sequential_Labels_And_Stable_Ids()
    {
        await using var factory = new CustomerQuestionnaireWebApplicationFactory();
        var seeded = await SeedAssessmentAsync(factory, "3.0");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/assessment/questionnaire/{seeded.InvitationId:D}/form?token={seeded.Token}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sections: 6 · Questions: 36", html, StringComparison.Ordinal);
        Assert.Equal(36, Regex.Matches(html, "class=\\\"[^\\\"]*\\baq-question\\b").Count);
        AssertQuestionNumbers(html, 36);
        AssertDisplayNumber(html, "q33", "Q17");
        AssertDisplayNumber(html, "q34", "Q18");
        AssertDisplayNumber(html, "q35", "Q25");
        AssertDisplayNumber(html, "q36", "Q26");
        AssertQuestionDisplayNumber(html, "q33", "Q17");
        AssertQuestionDisplayNumber(html, "q34", "Q18");
        AssertQuestionDisplayNumber(html, "q35", "Q25");
        AssertQuestionDisplayNumber(html, "q36", "Q26");
        AssertOrdered(html, "data-question-id=\"q33\"", "data-question-id=\"q34\"", "data-question-id=\"q17\"");
        Assert.Contains("data-question-id=\"q33\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"q33_nearly_all\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"q33\"", html, StringComparison.Ordinal);
        Assert.Contains("data-question-id=\"q34\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"q34\"", html, StringComparison.Ordinal);
        Assert.Contains("data-question-id=\"q35\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"q35\"", html, StringComparison.Ordinal);
        Assert.Contains("data-question-id=\"q36\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"q36\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"q34_consistently_reliable\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"q35_systematic_monitoring\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"q36_almost_never_missing\"", html, StringComparison.Ordinal);
    }

    private static void AssertQuestionNumbers(string html, int count)
    {
        var numbers = Regex.Matches(html, "<h3 class=\\\"h6 fw-semibold mb-3\\\">\\s*(?<number>Q\\d{2}) —")
            .Select(match => match.Groups["number"].Value)
            .ToArray();

        Assert.Equal(Enumerable.Range(1, count).Select(number => $"Q{number:D2}"), numbers);
    }

    private static void AssertDisplayNumber(string html, string questionId, string expectedNumber)
    {
        var match = Regex.Match(html, $"<article[^>]*data-question-id=\\\"{questionId}\\\".*?<h3[^>]*>\\s*(?<number>Q\\d{{2}}) —", RegexOptions.Singleline);

        Assert.True(match.Success, $"Expected a rendered heading for {questionId}.");
        Assert.Equal(expectedNumber, match.Groups["number"].Value);
    }

    private static void AssertQuestionDisplayNumber(string html, string questionId, string expectedNumber)
    {
        var match = Regex.Match(
            html,
            $"<article[^>]*data-question-id=\\\"{Regex.Escape(questionId)}\\\"[^>]*data-question-number=\\\"(?<number>Q\\d{{2}})\\\"",
            RegexOptions.Singleline);

        Assert.True(match.Success, $"Expected a rendered display number for {questionId}.");
        Assert.Equal(expectedNumber, match.Groups["number"].Value);
    }

    private static void AssertQuestionChoice(string html, string questionId, string value, string label)
    {
        Assert.Contains($"id=\"{questionId}_{value}\"", html, StringComparison.Ordinal);
        Assert.Contains($"value=\"{value}\"", html, StringComparison.Ordinal);
        Assert.Contains(label, html, StringComparison.Ordinal);
    }

    private static void AssertOrdered(string html, params string[] fragments)
    {
        var previousIndex = -1;
        foreach (var fragment in fragments)
        {
            var index = html.IndexOf(fragment, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Expected '{fragment}' after index {previousIndex}.");
            previousIndex = index;
        }
    }

    private static JsonDocument ReadInitialResponse(string html)
    {
        var match = Regex.Match(html, "<script type=\"application/json\" id=\"questionnaire-initial-response-json\">(?<json>.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success, "The rendered questionnaire must include its initial response JSON.");
        return JsonDocument.Parse(match.Groups["json"].Value);
    }

    private static async Task<(Guid InvitationId, string Token)> SeedAssessmentAsync(
        CustomerQuestionnaireWebApplicationFactory factory,
        string questionnaireVersion,
        QuestionnaireResponseV1? draft = null)
    {
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var token = $"customer-token-{Guid.NewGuid():N}";

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Status = AssessmentStatus.InProgress,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                QuestionnaireVersion = questionnaireVersion
            });
            db.Set<AssessmentInvitation>().Add(new AssessmentInvitation
            {
                Id = invitationId,
                AssessmentId = assessmentId,
                Email = "customer@example.com",
                TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(),
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddHours(2)
            });
            if (draft is not null)
            {
                db.Set<AssessmentResponse>().Add(new AssessmentResponse
                {
                    Id = Guid.NewGuid(),
                    AssessmentId = assessmentId,
                    InvitationId = invitationId,
                    Data = QuestionnaireResponseV1Serializer.Serialize(draft),
                    IsDraft = true,
                    LastSavedUtc = DateTime.UtcNow,
                    ConcurrencyStamp = "draft-stamp"
                });
            }

            return Task.CompletedTask;
        });

        return (invitationId, token);
    }

    private sealed class CustomerQuestionnaireWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"customer-questionnaire-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused;Username=unused;Password=unused"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<ApplicationDbContext>();
                services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            });
        }

        public async Task SeedAsync(Func<ApplicationDbContext, Task> seed)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await seed(db);
            await db.SaveChangesAsync();
        }
    }
}
