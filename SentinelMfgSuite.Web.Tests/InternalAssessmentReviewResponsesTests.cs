using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class InternalAssessmentReviewResponsesTests
{
    [Fact]
    public async Task ReviewResponses_Anonymous_Is_Challenged()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReviewResponses_Authenticated_NonAdmin_Is_Forbidden()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "user");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReviewResponses_TenantAdmin_Alone_Is_Forbidden()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "tenantadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReviewResponses_AssessmentAdmin_Renders_All_Sections_And_Questions()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "Submitted Assessment",
                SiteName = "Plant South",
                ContactEmail = "customer@example.com",
                Status = AssessmentStatus.Submitted,
                CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            QuestionnaireVersion = "1.0"
            });

            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                InvitationId = invitationId,
                Data = "{\"schemaVersion\":\"1.0\",\"answers\":{\"q04\":{\"selected\":\"1-5\"}}}",
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow,
                ConcurrencyStamp = Guid.NewGuid().ToString()
            });

            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Q04", html, StringComparison.Ordinal);
        Assert.Contains("Q30", html, StringComparison.Ordinal);
        Assert.Contains("Questions: 30", html, StringComparison.Ordinal);
        Assert.Contains("q04_1-5", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewResponses_V1_Does_Not_Render_V2_Questions_Or_Modify_Stored_Response()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        const string originalData = "{\"schemaVersion\":\"1.0\",\"answers\":{\"q04\":{\"selected\":\"1-5\"}}}";

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Status = AssessmentStatus.Submitted,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                QuestionnaireVersion = "1.0"
            });
            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = responseId,
                AssessmentId = assessmentId,
                InvitationId = Guid.NewGuid(),
                Data = originalData,
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow,
                ConcurrencyStamp = "v1-review-stamp"
            });
            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Questions: 30", html, StringComparison.Ordinal);
        Assert.Contains("data-question-id=\"q30\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-question-id=\"q31\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-question-id=\"q32\"", html, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .Set<AssessmentResponse>().AsNoTracking().SingleAsync(item => item.Id == responseId);
        Assert.Equal(originalData, persisted.Data);
        Assert.Equal("v1-review-stamp", persisted.ConcurrencyStamp);
    }

    [Fact]
    public async Task ReviewResponses_V2_Renders_Q31_Q32_Answers_ReadOnly_Without_Modifying_Stored_Response()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        const string originalData = "{\"schemaVersion\":\"2.0\",\"answers\":{\"q31\":{\"selected\":\"generally_accurate\"},\"q32\":{\"selected\":\"investigate_improve\"}}}";
        var responseId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Status = AssessmentStatus.Submitted,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                QuestionnaireVersion = "2.0"
            });
            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = responseId,
                AssessmentId = assessmentId,
                InvitationId = Guid.NewGuid(),
                Data = originalData,
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow,
                ConcurrencyStamp = "v2-review-stamp"
            });
            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Questions: 32", html, StringComparison.Ordinal);
        Assert.Contains("data-question-id=\"q31\"", html, StringComparison.Ordinal);
        Assert.Contains("data-question-id=\"q32\"", html, StringComparison.Ordinal);
        Assert.Contains("Q27 — How consistently do operational reports and KPIs reflect actual production conditions?", html, StringComparison.Ordinal);
        Assert.Contains("Q28 — How are operational data and analytics currently used to support decisions?", html, StringComparison.Ordinal);
        Assert.Contains("Q29 — What measurable operational outcome would make this assessment valuable?", html, StringComparison.Ordinal);
        Assert.Contains("id=\"q31_generally_accurate\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"q32_investigate_improve\"", html, StringComparison.Ordinal);
        using var initialResponse = ReadInitialResponse(html);
        Assert.Equal("generally_accurate", initialResponse.RootElement.GetProperty("answers").GetProperty("q31").GetProperty("selected").GetString());
        Assert.Equal("investigate_improve", initialResponse.RootElement.GetProperty("answers").GetProperty("q32").GetProperty("selected").GetString());
        Assert.DoesNotContain("id=\"saveDraftBtn\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"submitBtn\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/submit?token=", html, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .Set<AssessmentResponse>().AsNoTracking().SingleAsync(item => item.Id == responseId);
        Assert.Equal(originalData, persisted.Data);
        Assert.Equal("v2-review-stamp", persisted.ConcurrencyStamp);
    }

    [Fact]
    public async Task ReviewResponses_V3_Renders_New_Answers_ReadOnly_Without_Modifying_Stored_Response()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        const string originalData = "{\"schemaVersion\":\"3.0\",\"answers\":{\"q33\":{\"selected\":\"nearly_all\"},\"q34\":{\"selected\":\"usually_on_time\"},\"q35\":{\"selected\":\"routine_checks\"},\"q36\":{\"selected\":\"rarely_missing\"}}}";
        var responseId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Status = AssessmentStatus.Submitted,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                QuestionnaireVersion = "3.0"
            });
            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = responseId,
                AssessmentId = assessmentId,
                InvitationId = Guid.NewGuid(),
                Data = originalData,
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow,
                ConcurrencyStamp = "v3-review-stamp"
            });
            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Questions: 36", html, StringComparison.Ordinal);
        Assert.Contains("Q17 — For the equipment and systems relevant", html, StringComparison.Ordinal);
        Assert.Contains("Q18 — For those automatic connections", html, StringComparison.Ordinal);
        Assert.Contains("Q25 — How are important operational data values checked", html, StringComparison.Ordinal);
        Assert.Contains("Q26 — For the operational data needed", html, StringComparison.Ordinal);
        Assert.Contains("data-question-id=\"q33\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"q33_nearly_all\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"q34_usually_on_time\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"q35_routine_checks\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"q36_rarely_missing\"", html, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .Set<AssessmentResponse>().AsNoTracking().SingleAsync(item => item.Id == responseId);
        Assert.Equal(originalData, persisted.Data);
        Assert.Equal("v3-review-stamp", persisted.ConcurrencyStamp);
    }

    [Fact]
    public async Task ReviewResponses_V3_Renders_Eligible_Scores_Coverage_And_Consultant_Flags()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        const string originalData = "{\"schemaVersion\":\"3.0\",\"answers\":{\"q15\":{\"composite\":{\"integration_level\":{\"selected\":\"mostly\"}}},\"q17\":{\"composite\":{\"history_choice\":{\"selected\":\"some_sources\"}}},\"q19\":{\"selected\":\"yes\"},\"q20\":{\"selected\":\"standardized\"},\"q21\":{\"selected\":\"automatic\"},\"q24\":{\"selected\":\"automated_validated\"},\"q31\":{\"selected\":\"consistently_accurate\"},\"q32\":{\"selected\":\"proactive_decisions\"},\"q33\":{\"selected\":\"none\"},\"q34\":{\"selected\":\"usually_on_time\"},\"q35\":{\"selected\":\"systematic_monitoring\"},\"q36\":{\"selected\":\"almost_never_missing\"}}}";

        await SeedSubmittedResponseAsync(factory, assessmentId, "3.0", originalData);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");
        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Readiness Scoring Summary", html, StringComparison.Ordinal);
        Assert.Contains("Overall readiness:", html, StringComparison.Ordinal);
        Assert.Contains("class=\"table table-dark table-bordered table-sm align-middle mb-3\"", html, StringComparison.Ordinal);
        Assert.Contains("D1 — Connectivity &amp; Integration", html, StringComparison.Ordinal);
        Assert.Contains("D2 — Data Quality &amp; Contextualization", html, StringComparison.Ordinal);
        Assert.Contains("D3 — Analytics &amp; Reporting", html, StringComparison.Ordinal);
        Assert.Contains("100/100", html, StringComparison.Ordinal);
        Assert.Contains("3/3 (100%)", html, StringComparison.Ordinal);
        Assert.Contains("45%", html, StringComparison.Ordinal);
        Assert.Contains("ConnectivityReliabilityConflict", html, StringComparison.Ordinal);
        Assert.Contains("Scores are based on self-reported responses and have not been independently verified.", html, StringComparison.Ordinal);
        Assert.Contains("id=\"q33_nearly_all\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewResponses_V3_Shows_Insufficient_Evidence_And_Item_Details()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        const string responseData = "{\"schemaVersion\":\"3.0\",\"answers\":{\"q34\":{\"selected\":\"usually_on_time\"}}}";
        await SeedSubmittedResponseAsync(factory, assessmentId, "3.0", responseData);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");
        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Insufficient Evidence", html, StringComparison.Ordinal);
        Assert.Contains("Evidence requiring attention", html, StringComparison.Ordinal);
        Assert.Contains("Q15 &#x2014; Integration level: no scoreable answer was provided", html, StringComparison.Ordinal);
        Assert.Contains("Q17 &#x2014; For the equipment and systems relevant to your priority operational challenge", html, StringComparison.Ordinal);
        Assert.DoesNotContain("q15.integration_level", html, StringComparison.Ordinal);
        Assert.DoesNotContain("q33:", html, StringComparison.Ordinal);
        Assert.Contains("Overall scoring requires all three dimensions", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewResponses_V1_Shows_Historical_Scoring_Message()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        await SeedSubmittedResponseAsync(factory, assessmentId, "1.0", "{\"schemaVersion\":\"1.0\",\"answers\":{}}");

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");
        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Historical Assessment — Scoring Not Available", html, StringComparison.Ordinal);
        Assert.Contains("Scores are based on self-reported responses and have not been independently verified.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("D1 — Connectivity &amp; Integration", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewResponses_V2_Explains_Overall_Scoring_Is_Unavailable()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        const string responseData = "{\"schemaVersion\":\"2.0\",\"answers\":{\"q17\":{\"composite\":{\"history_choice\":{\"selected\":\"some_sources\"}}},\"q19\":{\"selected\":\"yes\"},\"q20\":{\"selected\":\"standardized\"},\"q21\":{\"selected\":\"automatic\"},\"q24\":{\"selected\":\"automated_validated\"},\"q31\":{\"selected\":\"consistently_accurate\"},\"q32\":{\"selected\":\"proactive_decisions\"}}}";
        await SeedSubmittedResponseAsync(factory, assessmentId, "2.0", responseData);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");
        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Overall scoring is unavailable for V2 because Connectivity &amp; Integration cannot be scored under V2.", html, StringComparison.Ordinal);
        Assert.Contains("D1 — Connectivity &amp; Integration", html, StringComparison.Ordinal);
        Assert.Contains("Not scored for this version", html, StringComparison.Ordinal);
        Assert.Contains("4/4 (100%)", html, StringComparison.Ordinal);
    }

    private static async Task SeedSubmittedResponseAsync(InternalReviewWebApplicationFactory factory, Guid assessmentId, string version, string data)
    {
        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Status = AssessmentStatus.Submitted,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                QuestionnaireVersion = version
            });
            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                InvitationId = Guid.NewGuid(),
                Data = data,
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow,
                ConcurrencyStamp = Guid.NewGuid().ToString()
            });
            return Task.CompletedTask;
        });
    }

    private static JsonDocument ReadInitialResponse(string html)
    {
        var match = Regex.Match(html, "<script type=\"application/json\" id=\"questionnaire-initial-response-json\">(?<json>.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success, "The rendered review must include its initial response JSON.");
        return JsonDocument.Parse(match.Groups["json"].Value);
    }

    [Theory]
    [InlineData("4.0")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ReviewResponses_Unsupported_Or_Blank_Assessment_Version_Shows_Unavailable(string version)
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Status = AssessmentStatus.Submitted,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                QuestionnaireVersion = version
            });
            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                InvitationId = Guid.NewGuid(),
                Data = "{\"schemaVersion\":\"1.0\",\"answers\":{}}",
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow,
                ConcurrencyStamp = Guid.NewGuid().ToString()
            });
            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("questionnaire version is not available for review", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-question-id=\"q01\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Questions: 30", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Unsupported questionnaire version", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewResponses_Uses_Assessment_Version_Not_Response_Version_To_Select_Definition()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Status = AssessmentStatus.Submitted,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                QuestionnaireVersion = string.Empty
            });
            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                InvitationId = Guid.NewGuid(),
                Data = "{\"schemaVersion\":\"1.0\",\"answers\":{}}",
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow,
                ConcurrencyStamp = Guid.NewGuid().ToString()
            });
            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("questionnaire version is not available for review", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-question-id=\"q01\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewResponses_Response_Schema_Mismatch_Shows_Unavailable_Without_Rewriting()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        const string originalData = "{\"schemaVersion\":\"2.0\",\"answers\":{\"q04\":{\"selected\":\"1-5\"}}}";

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Status = AssessmentStatus.Submitted,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                QuestionnaireVersion = "1.0"
            });
            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                InvitationId = Guid.NewGuid(),
                Data = originalData,
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow,
                ConcurrencyStamp = "mismatch-stamp"
            });
            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("does not match this assessment", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-question-id=\"q01\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Questions: 30", html, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Set<AssessmentResponse>().AsNoTracking().SingleAsync(r => r.AssessmentId == assessmentId);
        Assert.Equal(originalData, stored.Data);
        Assert.Equal("mismatch-stamp", stored.ConcurrencyStamp);
    }

    [Fact]
    public async Task ReviewResponses_Hides_Save_Submit_And_Token()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "Submitted Assessment",
                SiteName = "Plant South",
                ContactEmail = "customer@example.com",
                Status = AssessmentStatus.Submitted,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                InvitationId = invitationId,
                Data = "{\"schemaVersion\":\"1.0\",\"answers\":{}}",
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow,
                ConcurrencyStamp = Guid.NewGuid().ToString()
            });

            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("id=\"saveDraftBtn\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"submitBtn\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("response-json-should-never-render", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewResponses_Missing_Response_Shows_Unavailable_State()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "No Response Yet",
                SiteName = "Plant East",
                ContactEmail = "customer2@example.com",
                Status = AssessmentStatus.Invited,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("does not have a submitted questionnaire response yet", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewResponses_Malformed_Json_Shows_Unavailable_Not_Empty()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "Malformed Data Assessment",
                SiteName = "Plant West",
                ContactEmail = "customer3@example.com",
                Status = AssessmentStatus.Submitted,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                InvitationId = invitationId,
                Data = "{not-valid-json",
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow,
                ConcurrencyStamp = Guid.NewGuid().ToString()
            });

            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("could not be read", html, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Set<AssessmentResponse>().AsNoTracking().SingleAsync(r => r.AssessmentId == assessmentId);
        Assert.Equal("{not-valid-json", stored.Data);
    }

    [Fact]
    public async Task Dashboard_Shows_Review_Responses_Link_Only_For_Submitted_Assessments()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var submittedAssessmentId = Guid.NewGuid();
        var inProgressAssessmentId = Guid.NewGuid();

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = submittedAssessmentId,
                Title = "Submitted Assessment Row",
                SiteName = "Plant South",
                ContactEmail = "customer@example.com",
                Status = AssessmentStatus.Submitted,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = Guid.NewGuid(),
                AssessmentId = submittedAssessmentId,
                InvitationId = Guid.NewGuid(),
                Data = "{\"schemaVersion\":\"1.0\",\"answers\":{}}",
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow,
                ConcurrencyStamp = Guid.NewGuid().ToString()
            });

            db.Set<Assessment>().Add(new Assessment
            {
                Id = inProgressAssessmentId,
                Title = "In Progress Assessment Row",
                SiteName = "Plant North",
                ContactEmail = "customer4@example.com",
                Status = AssessmentStatus.InProgress,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = Guid.NewGuid(),
                AssessmentId = inProgressAssessmentId,
                InvitationId = Guid.NewGuid(),
                Data = "{\"schemaVersion\":\"1.0\",\"answers\":{}}",
                IsDraft = true,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = null,
                ConcurrencyStamp = Guid.NewGuid().ToString()
            });

            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync("/Internal/Assessments");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"/Internal/Assessments/{submittedAssessmentId}/ReviewResponses", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"/Internal/Assessments/{inProgressAssessmentId}/ReviewResponses", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewResponses_Does_Not_Write_To_Database()
    {
        await using var factory = new InternalReviewWebApplicationFactory();
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        const string originalData = "{\"schemaVersion\":\"1.0\",\"answers\":{\"q01\":{\"composite\":{\"facility_name\":{\"text\":\"Plant A\"}}}}}";

        await factory.SeedAsync(db =>
        {
            db.Set<Assessment>().Add(new Assessment
            {
                Id = assessmentId,
                Title = "Write Guard Assessment",
                SiteName = "Plant Guard",
                ContactEmail = "guard@example.com",
                Status = AssessmentStatus.Submitted,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });

            db.Set<AssessmentResponse>().Add(new AssessmentResponse
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                InvitationId = invitationId,
                Data = originalData,
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow,
                ConcurrencyStamp = "original-stamp"
            });

            return Task.CompletedTask;
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");

        var response = await client.GetAsync($"/Internal/Assessments/{assessmentId}/ReviewResponses");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Set<AssessmentResponse>().AsNoTracking().SingleAsync(r => r.AssessmentId == assessmentId);
        Assert.Equal(originalData, stored.Data);
        Assert.Equal("original-stamp", stored.ConcurrencyStamp);
    }

    private sealed class InternalReviewWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"internal-review-{Guid.NewGuid():N}";

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

                services.AddDbContext<ApplicationDbContext>(options =>
                {
                    options.UseInMemoryDatabase(_databaseName);
                });

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
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

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestAuth";
        public const string HeaderName = "X-Test-Auth";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(HeaderName, out var values))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var mode = values.ToString();
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "test-user"),
                new(ClaimTypes.Name, "test-user")
            };

            if (string.Equals(mode, "assessmentadmin", StringComparison.OrdinalIgnoreCase))
            {
                claims.Add(new Claim(ClaimTypes.Role, "AssessmentAdmin"));
            }
            else if (string.Equals(mode, "tenantadmin", StringComparison.OrdinalIgnoreCase))
            {
                claims.Add(new Claim(ClaimTypes.Role, "TenantAdmin"));
            }

            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
