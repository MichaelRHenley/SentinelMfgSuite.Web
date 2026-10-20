using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
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
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class InternalAssessmentReportPageTests
{
    [Fact]
    public async Task ReportPage_Requires_AssessmentAdmin()
    {
        await using var factory = new ReportPageFactory();
        var ids = await factory.SeedAssessmentAsync("3.0", BuildSubmission("3.0"));
        var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var regular = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        regular.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "user");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(factory.PageUrl(ids.AssessmentId, ids.ResponseId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await regular.GetAsync(factory.PageUrl(ids.AssessmentId, ids.ResponseId))).StatusCode);
    }

    [Fact]
    public async Task ReportPage_Uses_Exact_Submitted_Response_Associated_With_Report()
    {
        await using var factory = new ReportPageFactory();
        var assessmentId = Guid.NewGuid();
        var old = await factory.SeedSubmittedResponseAsync(assessmentId, "older source marker", DateTime.UtcNow.AddDays(-1));
        var latest = await factory.SeedSubmittedResponseAsync(assessmentId, "newer unrelated marker", DateTime.UtcNow);
        await factory.SeedReportAsync(assessmentId, old.ResponseId, "3.0", BuildSubmission("3.0"));
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync(factory.PageUrl(assessmentId, old.ResponseId));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("older source marker", html, StringComparison.Ordinal);
        Assert.DoesNotContain("newer unrelated marker", html, StringComparison.Ordinal);
        Assert.NotEqual(old.ResponseId, latest.ResponseId);
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", response.Headers.GetValues("Cache-Control").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task ReportPage_Creates_Only_On_Explicit_Post_And_Is_Idempotent()
    {
        await using var factory = new ReportPageFactory();
        var ids = await factory.SeedAssessmentAsync("3.0", BuildSubmission("3.0"));
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);

        var get = await client.GetAsync(url);
        var html = await get.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Contains("Create Draft Report", html, StringComparison.Ordinal);
        Assert.Equal(0, await factory.CountReportsAsync());
        var tokenMatch = System.Text.RegularExpressions.Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.True(tokenMatch.Success);
        var token = WebUtility.HtmlDecode(tokenMatch.Groups["token"].Value);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString()
        });

        var first = await client.PostAsync(url + "?handler=CreateDraft", form);
        var second = await client.PostAsync(url + "?handler=CreateDraft", form);

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        var created = await factory.GetOnlyReportAsync();
        Assert.Equal(ids.ResponseId, created.SubmittedResponseId);
        Assert.Single(created.Revisions);
        Assert.Equal(1, created.Revisions.Single().RevisionNumber);
        Assert.Equal(AssessmentReportRevisionStatus.Draft, created.Revisions.Single().Status);
    }

    [Fact]
    public async Task ReportPage_Create_Post_Requires_Antiforgery_Token()
    {
        await using var factory = new ReportPageFactory();
        var ids = await factory.SeedAssessmentAsync("3.0", BuildSubmission("3.0"));
        var client = factory.CreateAdminClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString()
        });

        var response = await client.PostAsync(factory.PageUrl(ids.AssessmentId, ids.ResponseId) + "?handler=CreateDraft", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await factory.CountReportsAsync());
    }

    [Fact]
    public async Task ReportPage_Renders_Withheld_Scoring_Without_Numeric_Values()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("2.0");
        var ids = await factory.SeedAssessmentAsync("2.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "2.0", submission);
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync(factory.PageUrl(ids.AssessmentId, ids.ResponseId));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Withheld", html, StringComparison.Ordinal);
        Assert.Contains("No numeric score or classification is assigned.", html, StringComparison.Ordinal);
        Assert.Contains("OverallNotAvailableForV2", html, StringComparison.Ordinal);
        Assert.Contains("Not scored", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportPage_Rejects_Owner_Mismatch()
    {
        await using var factory = new ReportPageFactory(ownerUserId: "different-user");
        var ids = await factory.SeedAssessmentAsync("3.0", BuildSubmission("3.0"));
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync(factory.PageUrl(ids.AssessmentId, ids.ResponseId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExecutiveSummary_Save_Reloads_And_Preserves_Other_Sections_And_Source_Snapshot()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var page = await client.GetAsync(url);
        var html = await page.Content.ReadAsStringAsync();
        var token = ExtractToken(html);
        var report = await factory.GetOnlyReportAsync();
        var revision = report.Revisions.Single();
        var stamp = revision.ConcurrencyStamp;
        var originalResponse = (await factory.GetResponseAsync(ids.ResponseId)).Data;
        var originalSnapshot = revision.ScoringSnapshot;
        var existing = ConsultantReportContent.CreateEmpty() with
        {
            IndustrialDataAndSystems = new IndustrialDataAndSystemsSection(
                ReportAssessmentState.Assessed,
                Systems: new[] { new SystemAssessmentEntry("sys-1", "MES", ReportAssessmentState.Assessed, null, null, null, null, null, null,
                    ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.Unverified, Array.Empty<ReportEvidenceReference>()) })
        };
        await factory.SetContentAsync(revision.Id, existing);
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        stamp = revision.ConcurrencyStamp;

        var response = await client.PostAsync(url + "?handler=SaveExecutiveSummary", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString(),
            ["RevisionId"] = revision.Id.ToString(),
            ["ExpectedConcurrencyStamp"] = stamp.ToString(),
            ["Input.State"] = nameof(ReportAssessmentState.Assessed),
            ["Input.Narrative"] = "A carefully qualified narrative. <script>alert(1)</script>"
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var savedRevision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var saved = ConsultantReportContentSerializer.Deserialize(savedRevision.ConsultantContentJson!);
        Assert.Equal("A carefully qualified narrative. <script>alert(1)</script>", saved.ExecutiveSummary.Narrative);
        Assert.Equal(existing.IndustrialDataAndSystems.State, saved.IndustrialDataAndSystems.State);
        Assert.Equal(existing.IndustrialDataAndSystems.Systems!.Single().Id, saved.IndustrialDataAndSystems.Systems!.Single().Id);
        Assert.Equal(existing.IndustrialDataAndSystems.Systems!.Single().Name, saved.IndustrialDataAndSystems.Systems!.Single().Name);
        Assert.Equal(originalResponse, (await factory.GetResponseAsync(ids.ResponseId)).Data);
        Assert.Equal(originalSnapshot, savedRevision.ScoringSnapshot);
        var reload = await client.GetAsync(url);
        var reloadedHtml = await reload.Content.ReadAsStringAsync();
        Assert.Contains("A carefully qualified narrative. &lt;script&gt;alert(1)&lt;/script&gt;", reloadedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", reloadedHtml, StringComparison.Ordinal);
        Assert.Contains("Last saved:", reloadedHtml, StringComparison.Ordinal);
        Assert.Contains("Executive Summary Draft saved.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutiveSummary_Save_Rejects_Structural_Contradiction_And_Retains_Input()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();

        var response = await client.PostAsync(url + "?handler=SaveExecutiveSummary", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(html),
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString(),
            ["RevisionId"] = revision.Id.ToString(),
            ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
            ["Input.State"] = nameof(ReportAssessmentState.NotAssessed),
            ["Input.Narrative"] = "Keep this text even after failure."
        }));
        var resultHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("must not contain assessed content", resultHtml, StringComparison.Ordinal);
        Assert.Contains("Keep this text even after failure.", resultHtml, StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);
    }

    [Fact]
    public async Task IndustrialData_Save_Adds_And_Edits_System_With_Stable_Id_And_Preserves_Untouched_Content()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var preservedSystem = new SystemAssessmentEntry("original-system", "Existing PLC", ReportAssessmentState.Assessed,
            "PLC", "Operations", "Line 1", "Signals", "OPC UA", "High", ReportContentProvenance.QuestionnaireDerived,
            ReportVerificationStatus.SelfReported, new[] { new ReportEvidenceReference(ReportEvidenceKind.QuestionnaireAnswer, "q11", QuestionId: "q11") }, "Keep these notes.");
        var preservedFinding = new ReportFinding("finding-preserved", "Existing", "Keep finding", ReportFindingClassification.PreliminaryFinding,
            ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.Unverified, Array.Empty<ReportEvidenceReference>());
        var preservedEvidence = new ReportEvidenceReference(ReportEvidenceKind.ScoringDimension, "D1");
        var otherContent = ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(ReportAssessmentState.Assessed, "Keep summary"),
            IndustrialDataAndSystems = new IndustrialDataAndSystemsSection(ReportAssessmentState.Assessed,
                new[] { preservedSystem }, new[] { preservedFinding }, new[] { preservedEvidence }),
            AiOpportunitiesAndRoi = new AiOpportunitiesAndRoiSection(ReportAssessmentState.Assessed)
        };
        await factory.SetContentAsync(revision.Id, otherContent);
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var initialHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var token = ExtractToken(initialHtml);
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString(),
            ["RevisionId"] = revision.Id.ToString(),
            ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
            ["IndustrialInput.State"] = nameof(ReportAssessmentState.Assessed),
            ["IndustrialInput.Systems[0].Id"] = preservedSystem.Id,
            ["IndustrialInput.Systems[0].Name"] = "Edited PLC <script>alert(1)</script>",
            ["IndustrialInput.Systems[0].AssessmentState"] = nameof(ReportAssessmentState.Assessed),
            ["IndustrialInput.Systems[0].SystemType"] = "PLC",
            ["IndustrialInput.Systems[0].Owner"] = "Updated owner",
            ["IndustrialInput.Systems[0].OperationalArea"] = "Line 2",
            ["IndustrialInput.Systems[0].DataExchanged"] = "Signals and status",
            ["IndustrialInput.Systems[0].IntegrationPattern"] = "OPC UA",
            ["IndustrialInput.Systems[0].Criticality"] = "High",
            ["IndustrialInput.Systems[0].ConsultantNotes"] = "Edited notes",
            ["IndustrialInput.Systems[1].Id"] = string.Empty,
            ["IndustrialInput.Systems[1].Name"] = "New MES",
            ["IndustrialInput.Systems[1].AssessmentState"] = nameof(ReportAssessmentState.NotAssessed),
            ["IndustrialInput.Systems[1].SystemType"] = "MES"
        };
        var firstSave = await client.PostAsync(url + "?handler=SaveIndustrialData", new FormUrlEncodedContent(form));
        var firstHtml = await firstSave.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, firstSave.StatusCode);
        Assert.Contains("Industrial Data &amp; Systems Draft saved.", firstHtml, StringComparison.Ordinal);
        Assert.Contains("Edited PLC &lt;script&gt;alert(1)&lt;/script&gt;", firstHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", firstHtml, StringComparison.Ordinal);
        var savedRevision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var saved = ConsultantReportContentSerializer.Deserialize(savedRevision.ConsultantContentJson!);
        Assert.Equal("original-system", saved.IndustrialDataAndSystems.Systems![0].Id);
        Assert.Equal("Edited PLC <script>alert(1)</script>", saved.IndustrialDataAndSystems.Systems[0].Name);
        Assert.Equal("Updated owner", saved.IndustrialDataAndSystems.Systems[0].Owner);
        Assert.Equal(preservedSystem.Provenance, saved.IndustrialDataAndSystems.Systems[0].Provenance);
        Assert.Equal(preservedSystem.VerificationStatus, saved.IndustrialDataAndSystems.Systems[0].VerificationStatus);
        Assert.Equal(preservedSystem.EvidenceReferences, saved.IndustrialDataAndSystems.Systems[0].EvidenceReferences);
        var newSystem = Assert.Single(saved.IndustrialDataAndSystems.Systems.Skip(1));
        Assert.True(Guid.TryParseExact(newSystem.Id, "N", out _));
        Assert.Equal(ReportContentProvenance.ConsultantEntered, newSystem.Provenance);
        Assert.Equal(ReportVerificationStatus.Unverified, newSystem.VerificationStatus);
        Assert.Empty(newSystem.EvidenceReferences);
        Assert.Equal(preservedFinding.Id, saved.IndustrialDataAndSystems.Findings!.Single().Id);
        Assert.Equal(preservedEvidence, saved.IndustrialDataAndSystems.EvidenceReferences!.Single());
        Assert.Equal("Keep summary", saved.ExecutiveSummary.Narrative);
        Assert.Equal(otherContent.AiOpportunitiesAndRoi.State, saved.AiOpportunitiesAndRoi.State);

        savedRevision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var nextHtml = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var nextForm = new Dictionary<string, string>(form)
        {
            ["__RequestVerificationToken"] = ExtractToken(nextHtml),
            ["ExpectedConcurrencyStamp"] = savedRevision.ConcurrencyStamp.ToString(),
            ["IndustrialInput.Systems[1].Id"] = newSystem.Id,
            ["IndustrialInput.Systems[1].Name"] = "Renamed MES"
        };
        var secondSave = await client.PostAsync(url + "?handler=SaveIndustrialData", new FormUrlEncodedContent(nextForm));
        Assert.Equal(HttpStatusCode.OK, secondSave.StatusCode);
        var savedAgain = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        Assert.Equal(newSystem.Id, savedAgain.IndustrialDataAndSystems.Systems![1].Id);
        Assert.Equal("Renamed MES", savedAgain.IndustrialDataAndSystems.Systems[1].Name);
    }

    [Fact]
    public async Task IndustrialData_Save_Rejects_Empty_Duplicate_And_Unknown_System_Ids()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var existing = new SystemAssessmentEntry("existing-system", "Existing", ReportAssessmentState.Assessed,
            null, null, null, null, null, null, ReportContentProvenance.ConsultantEntered,
            ReportVerificationStatus.Unverified, Array.Empty<ReportEvidenceReference>());
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            IndustrialDataAndSystems = new IndustrialDataAndSystemsSection(ReportAssessmentState.Assessed, new[] { existing })
        });
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(html),
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString(),
            ["RevisionId"] = revision.Id.ToString(),
            ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
            ["IndustrialInput.State"] = nameof(ReportAssessmentState.Assessed),
            ["IndustrialInput.Systems[0].Id"] = existing.Id,
            ["IndustrialInput.Systems[0].Name"] = "Existing",
            ["IndustrialInput.Systems[0].AssessmentState"] = nameof(ReportAssessmentState.Assessed),
            ["IndustrialInput.Systems[1].Id"] = existing.Id,
            ["IndustrialInput.Systems[1].Name"] = "Duplicate",
            ["IndustrialInput.Systems[1].AssessmentState"] = nameof(ReportAssessmentState.Assessed),
            ["IndustrialInput.Systems[2].Id"] = "not-a-known-id",
            ["IndustrialInput.Systems[2].Name"] = "Unknown ID",
            ["IndustrialInput.Systems[2].AssessmentState"] = nameof(ReportAssessmentState.Assessed)
        };

        var response = await client.PostAsync(url + "?handler=SaveIndustrialData", new FormUrlEncodedContent(form));
        var resultHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("System IDs must be unique", resultHtml, StringComparison.Ordinal);
        Assert.Contains("System ID is invalid", resultHtml, StringComparison.Ordinal);
        Assert.Contains("Duplicate", resultHtml, StringComparison.Ordinal);
        var persisted = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        Assert.Equal("Existing", persisted.IndustrialDataAndSystems.Systems!.Single().Name);
    }

    [Fact]
    public async Task IndustrialData_Save_Rejects_NonAssessed_State_With_Systems_And_Retains_Inventory()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var response = await client.PostAsync(url + "?handler=SaveIndustrialData", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(html),
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString(),
            ["RevisionId"] = revision.Id.ToString(),
            ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
            ["IndustrialInput.State"] = nameof(ReportAssessmentState.NotAvailable),
            ["IndustrialInput.Systems[0].Id"] = string.Empty,
            ["IndustrialInput.Systems[0].Name"] = "Keep this system",
            ["IndustrialInput.Systems[0].AssessmentState"] = nameof(ReportAssessmentState.Assessed)
        }));
        var resultHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("must not contain assessed content", resultHtml, StringComparison.Ordinal);
        Assert.Contains("Keep this system", resultHtml, StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);
    }

    [Fact]
    public async Task IndustrialData_Save_Rejects_Stale_Token_Without_Overwriting_Newer_Inventory()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var newerSystem = new SystemAssessmentEntry("newer-system", "Newer inventory", ReportAssessmentState.Assessed,
            null, null, null, null, null, null, ReportContentProvenance.ConsultantEntered,
            ReportVerificationStatus.Unverified, Array.Empty<ReportEvidenceReference>());
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            IndustrialDataAndSystems = new IndustrialDataAndSystemsSection(ReportAssessmentState.Assessed, new[] { newerSystem })
        });
        var response = await client.PostAsync(url + "?handler=SaveIndustrialData", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(html),
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString(),
            ["RevisionId"] = revision.Id.ToString(),
            ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
            ["IndustrialInput.State"] = nameof(ReportAssessmentState.Assessed),
            ["IndustrialInput.Systems[0].Id"] = string.Empty,
            ["IndustrialInput.Systems[0].Name"] = "My unsaved system",
            ["IndustrialInput.Systems[0].AssessmentState"] = nameof(ReportAssessmentState.Assessed)
        }));
        var resultHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("changed after you opened it", resultHtml, StringComparison.Ordinal);
        Assert.Contains("My unsaved system", resultHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"Newer inventory\"", resultHtml, StringComparison.Ordinal);
        var saved = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        Assert.Equal("Newer inventory", saved.IndustrialDataAndSystems.Systems!.Single().Name);
    }

    [Fact]
    public async Task IndustrialData_Save_Rejects_Unauthorized_And_NonDraft_Requests()
    {
        await using var factory = new ReportPageFactory();
        var ids = await factory.SeedAssessmentAsync("3.0", BuildSubmission("3.0"));
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", BuildSubmission("3.0"));
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var regular = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        regular.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "user");
        Assert.Equal(HttpStatusCode.Forbidden, (await regular.PostAsync(url + "?handler=SaveIndustrialData", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);

        var client = factory.CreateAdminClient();
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Set<AssessmentReportRevision>().SingleAsync(item => item.Id == revision.Id);
            stored.Status = AssessmentReportRevisionStatus.Approved;
            await db.SaveChangesAsync();
        }
        var response = await client.PostAsync(url + "?handler=SaveIndustrialData", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(html),
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString(),
            ["RevisionId"] = revision.Id.ToString(),
            ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
            ["IndustrialInput.State"] = nameof(ReportAssessmentState.Assessed),
            ["IndustrialInput.Systems[0].Name"] = "Keep non-draft input"
        }));
        var resultHtml = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Only Draft report revisions can be edited", resultHtml, StringComparison.Ordinal);
        Assert.Contains("Keep non-draft input", resultHtml, StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);
    }

    [Fact]
    public async Task ExecutiveSummary_Save_Rejects_Invalid_Evidence_Reference_And_Retains_Input()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();

        var response = await client.PostAsync(url + "?handler=SaveExecutiveSummary", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(html),
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString(),
            ["RevisionId"] = revision.Id.ToString(),
            ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
            ["Input.State"] = nameof(ReportAssessmentState.Assessed),
            ["Input.Narrative"] = "Narrative stays after invalid evidence.",
            ["Input.EvidenceQuestionIds"] = "q-unknown"
        }));
        var resultHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("does not identify a source", resultHtml, StringComparison.Ordinal);
        Assert.Contains("Narrative stays after invalid evidence.", resultHtml, StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);
    }

    private static Dictionary<string, string> AiForm(Guid assessmentId, Guid responseId, AssessmentReportRevision revision, string html) => new()
    {
        ["__RequestVerificationToken"] = ExtractToken(html),
        ["AssessmentId"] = assessmentId.ToString(),
        ["SubmittedResponseId"] = responseId.ToString(),
        ["RevisionId"] = revision.Id.ToString(),
        ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
        ["AiInput.State"] = nameof(ReportAssessmentState.Assessed)
    };

    private static void AddOpportunity(Dictionary<string, string> form, string id, string problem)
    {
        form["AiInput.Opportunities[0].Id"] = id;
        form["AiInput.Opportunities[0].BusinessProblem"] = problem;
        form["AiInput.Opportunities[0].OperationalProcessOrArea"] = "Fictional Line 1";
        form["AiInput.Opportunities[0].ProposedUseCase"] = "Fictional vision inspection";
        form["AiInput.Opportunities[0].ExpectedMeasurableOutcome"] = "Fewer escapes";
        form["AiInput.Opportunities[0].RequiredDataSourcesText"] = "Camera feed\n\n  Defect log  \n";
        form["AiInput.Opportunities[0].Provenance"] = nameof(ReportContentProvenance.ConsultantEntered);
        form["AiInput.Opportunities[0].VerificationStatus"] = nameof(ReportVerificationStatus.Unverified);
        form["AiInput.Opportunities[0].ScopeState"] = nameof(ReportAssessmentState.Assessed);
        form["AiInput.Opportunities[0].ScopeDescription"] = "Pilot cell";
        form["AiInput.Opportunities[0].ScopeInText"] = "Cell A";
        form["AiInput.Opportunities[0].EstimateStatus"] = nameof(ReportEstimateStatus.Estimated);
        form["AiInput.Opportunities[0].EffortValue"] = "12.5";
        form["AiInput.Opportunities[0].EffortUnit"] = "person-days";
        form["AiInput.Opportunities[0].CostAmount"] = "9000";
        form["AiInput.Opportunities[0].Currency"] = "USD";
        form["AiInput.Opportunities[0].EstimateBasis"] = "Fictional basis";
    }

    [Fact]
    public async Task AiOpportunities_Create_Edit_Reload_Preserve_Id_Estimate_And_Other_Sections()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(ReportAssessmentState.Assessed, "Keep summary")
        });
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var snapshot = revision.ScoringSnapshot;
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var form = AiForm(ids.AssessmentId, ids.ResponseId, revision, html);
        AddOpportunity(form, string.Empty, "Problem <b>one</b>");
        form["AiInput.Opportunities[0].EvidenceQuestionIds"] = "q02";

        var first = await client.PostAsync(url + "?handler=SaveAiOpportunities", new FormUrlEncodedContent(form));
        var firstHtml = await first.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("Problem &lt;b&gt;one&lt;/b&gt;", firstHtml, StringComparison.Ordinal);
        var saved = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        var opportunity = Assert.Single(saved.AiOpportunitiesAndRoi.Opportunities!);
        Assert.True(Guid.TryParseExact(opportunity.Id, "N", out _));
        Assert.Equal(new[] { "Camera feed", "Defect log" }, opportunity.RequiredDataSources);
        Assert.Empty(opportunity.TechnicalPrerequisites);
        Assert.Equal(ReportContentProvenance.ConsultantEntered, opportunity.Provenance);
        Assert.Equal(ReportVerificationStatus.Unverified, opportunity.VerificationStatus);
        Assert.Equal(ReportEstimateStatus.Estimated, opportunity.Estimate.Status);
        Assert.Equal(12.5m, opportunity.Estimate.EffortValue);
        Assert.Equal("Pilot cell", opportunity.Scope.Description);
        Assert.Equal("q02", Assert.Single(opportunity.EvidenceReferences).QuestionId);
        Assert.Equal("Keep summary", saved.ExecutiveSummary.Narrative);
        Assert.Equal(snapshot, (await factory.GetOnlyReportAsync()).Revisions.Single().ScoringSnapshot);

        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var reloaded = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        Assert.Contains(opportunity.Id, reloaded, StringComparison.Ordinal);
        var edit = AiForm(ids.AssessmentId, ids.ResponseId, revision, reloaded);
        edit["AiInput.Opportunities[0].Id"] = opportunity.Id;
        edit["AiInput.Opportunities[0].BusinessProblem"] = "Edited problem";
        edit["AiInput.Opportunities[0].EvidenceQuestionIds"] = "q02";
        var second = await client.PostAsync(url + "?handler=SaveAiOpportunities", new FormUrlEncodedContent(edit));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var savedAgain = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        var edited = Assert.Single(savedAgain.AiOpportunitiesAndRoi.Opportunities!);
        Assert.Equal(opportunity.Id, edited.Id);
        Assert.Equal("Edited problem", edited.BusinessProblem);
        Assert.Equal(12.5m, edited.Estimate.EffortValue);
        Assert.Equal("Pilot cell", edited.Scope.Description);
        Assert.Equal(new[] { "Camera feed", "Defect log" }, edited.RequiredDataSources);
        Assert.Equal("q02", Assert.Single(edited.EvidenceReferences).QuestionId);
    }

    [Fact]
    public async Task AiOpportunities_Estimate_Reset_Requires_Confirmation_And_Invalid_Input_Is_Rejected()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var existing = new AiImplementationOpportunity("opp-1", "Problem", "Area", "Use case", "Outcome",
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            new EffortCostEstimate(ReportEstimateStatus.Estimated, 5m, "days", 100m, "USD", "Basis"),
            new ImplementationScope(ReportAssessmentState.NotAssessed),
            ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.Unverified, Array.Empty<ReportEvidenceReference>());
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            AiOpportunitiesAndRoi = new AiOpportunitiesAndRoiSection(ReportAssessmentState.Assessed, new[] { existing })
        });
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var form = AiForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["AiInput.Opportunities[0].Id"] = "opp-1";
        form["AiInput.Opportunities[0].BusinessProblem"] = "Problem";
        form["AiInput.Opportunities[0].EstimateStatus"] = nameof(ReportEstimateStatus.NotEstimated);

        var blocked = await client.PostAsync(url + "?handler=SaveAiOpportunities", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        var unchanged = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        Assert.Equal(5m, unchanged.AiOpportunitiesAndRoi.Opportunities!.Single().Estimate.EffortValue);

        form["AiInput.Opportunities[0].EffortValue"] = "abc";
        var invalid = await client.PostAsync(url + "?handler=SaveAiOpportunities", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Equal(5m, ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!)
            .AiOpportunitiesAndRoi.Opportunities!.Single().Estimate.EffortValue);

        form.Remove("AiInput.Opportunities[0].EffortValue");
        form["AiInput.Opportunities[0].ConfirmEstimateReset"] = "true";
        var confirmed = await client.PostAsync(url + "?handler=SaveAiOpportunities", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var reset = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!)
            .AiOpportunitiesAndRoi.Opportunities!.Single();
        Assert.Equal(ReportEstimateStatus.NotEstimated, reset.Estimate.Status);
        Assert.Null(reset.Estimate.EffortValue);
        Assert.Null(reset.Estimate.CostAmount);
    }

    [Fact]
    public async Task AiOpportunities_Reject_Invalid_Evidence_Stale_Stamp_And_NonDraft_And_Unauthorized()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var form = AiForm(ids.AssessmentId, ids.ResponseId, revision, html);
        AddOpportunity(form, string.Empty, "Retained problem");
        form["AiInput.Opportunities[0].EvidenceQuestionIds"] = "q-unknown";

        var invalid = await client.PostAsync(url + "?handler=SaveAiOpportunities", new FormUrlEncodedContent(form));
        Assert.Contains("Retained problem", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);

        form.Remove("AiInput.Opportunities[0].EvidenceQuestionIds");
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty());
        var stale = await client.PostAsync(url + "?handler=SaveAiOpportunities", new FormUrlEncodedContent(form));
        Assert.Contains("changed after you opened it", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var noToken = await client.PostAsync(url + "?handler=SaveAiOpportunities", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        var regular = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        regular.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "user");
        Assert.Equal(HttpStatusCode.Forbidden, (await regular.PostAsync(url + "?handler=SaveAiOpportunities", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Set<AssessmentReportRevision>().SingleAsync(item => item.Id == revision.Id);
            stored.Status = AssessmentReportRevisionStatus.Approved;
            await db.SaveChangesAsync();
        }
        var fresh = (await factory.GetOnlyReportAsync()).Revisions.Single();
        form["ExpectedConcurrencyStamp"] = fresh.ConcurrencyStamp.ToString();
        var nonDraft = await client.PostAsync(url + "?handler=SaveAiOpportunities", new FormUrlEncodedContent(form));
        Assert.Contains("Only Draft report revisions can be edited", await nonDraft.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportPage_Shows_Current_Banner_Description_And_AI_Form_Anchor()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("Editing, review, approval", html, StringComparison.Ordinal);
        Assert.Contains("Draft editing is available", html, StringComparison.Ordinal);
        Assert.Contains("Approval, issuance, PDF generation, and customer delivery are not yet available", html, StringComparison.Ordinal);
        Assert.Contains("Edit the Executive Summary, Industrial Data &amp; Systems, AI Opportunities &amp; ROI, Technical Gaps &amp; Architecture, Implementation Roadmap, and Executive Presentation Drafts", html, StringComparison.Ordinal);
        Assert.Contains("id=\"executivePresentation\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("editor not yet available", html, StringComparison.Ordinal);
        Assert.Contains("id=\"roadmap\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"technicalGaps\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"aiOpportunities\"", html, StringComparison.Ordinal);
        Assert.Contains("#aiOpportunities", html, StringComparison.Ordinal);

        var form = AiForm(ids.AssessmentId, ids.ResponseId, revision, html);
        AddOpportunity(form, string.Empty, "Anchor problem");
        var saved = await (await client.PostAsync(url + "?handler=SaveAiOpportunities", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();
        var sectionStart = saved.IndexOf("id=\"aiOpportunities\"", StringComparison.Ordinal);
        var successIndex = saved.IndexOf("AI Opportunities &amp; ROI Draft saved.", StringComparison.Ordinal);
        Assert.True(sectionStart >= 0 && successIndex > sectionStart);
        Assert.Equal(1, saved.Split("AI Opportunities &amp; ROI Draft saved.").Length - 1);
    }

    private static Dictionary<string, string> PresentationForm(Guid assessmentId, Guid responseId, AssessmentReportRevision revision, string html) => new()
    {
        ["__RequestVerificationToken"] = ExtractToken(html),
        ["AssessmentId"] = assessmentId.ToString(),
        ["SubmittedResponseId"] = responseId.ToString(),
        ["RevisionId"] = revision.Id.ToString(),
        ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
        ["PresentationInput.State"] = nameof(ReportAssessmentState.Assessed)
    };

    private static ReportTextEntry PresEntry(string id, string text) =>
        new(id, text, ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.Unverified,
            new[] { new ReportEvidenceReference(ReportEvidenceKind.ScoringDimension, "D1") });

    private async Task<(ReportPageFactory Factory, (Guid AssessmentId, Guid ResponseId) Ids, AssessmentReportRevision Revision, HttpClient Client, string Url, string Html)> PresentationSetupAsync(ExecutivePresentationSection? section)
    {
        var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        if (section is not null)
        {
            await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
            {
                ExecutiveSummary = new ExecutiveSummarySection(ReportAssessmentState.Assessed, "Keep summary"),
                ExecutivePresentation = section
            });
            revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        }
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        return (factory, (ids.AssessmentId, ids.ResponseId), revision, client, url, html);
    }

    [Fact]
    public async Task Presentation_Create_Edit_Order_Ids_Encoding_And_Clear()
    {
        var s = await PresentationSetupAsync(new ExecutivePresentationSection(ReportAssessmentState.Assessed, "Old", "Narr",
            new[] { PresEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Msg A") }, null, new[] { "Risk old" },
            new[] { new ReportEvidenceReference(ReportEvidenceKind.ScoringDimension, "D1") }));
        await using var factory = s.Factory;
        var snapshot = s.Revision.ScoringSnapshot;
        var form = PresentationForm(s.Ids.AssessmentId, s.Ids.ResponseId, s.Revision, s.Html);
        form["PresentationInput.Title"] = "New <b>title</b>";
        form["PresentationInput.TopRisksSubmitted"] = "true";
        form["PresentationInput.TopRisksText"] = "Risk one\r\n\r\nRisk two";
        form["PresentationInput.EvidenceSubmitted"] = "true";
        form["PresentationInput.EvidenceQuestionIds"] = "q02";
        form["PresentationInput.KeyMessages[0].Id"] = string.Empty;
        form["PresentationInput.KeyMessages[0].Text"] = "First new";
        form["PresentationInput.KeyMessages[1].Id"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        form["PresentationInput.KeyMessages[1].Text"] = "Edited <i>A</i>";
        form["PresentationInput.KeyMessages[1].EvidenceSubmitted"] = "true";
        form["PresentationInput.KeyMessages[1].EvidenceQuestionIds"] = "q02";
        form["PresentationInput.DecisionsRequested[0].Id"] = string.Empty;
        form["PresentationInput.DecisionsRequested[0].Text"] = "Approve pilot";
        var html = await (await s.Client.PostAsync(s.Url + "?handler=SaveExecutivePresentation", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();
        Assert.Contains("Executive Presentation Draft saved.", html, StringComparison.Ordinal);
        Assert.Contains("Edited &lt;i&gt;A&lt;/i&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("New <b>title</b>", html, StringComparison.Ordinal);

        var after = (await factory.GetOnlyReportAsync()).Revisions.Single();
        Assert.Equal(snapshot, after.ScoringSnapshot);
        var saved = ConsultantReportContentSerializer.Deserialize(after.ConsultantContentJson!);
        Assert.Equal("Keep summary", saved.ExecutiveSummary.Narrative);
        var p = saved.ExecutivePresentation;
        Assert.Equal(new[] { "Risk one", "Risk two" }, p.TopRisks);
        Assert.Equal(2, p.KeyMessages!.Count);
        Assert.Equal("First new", p.KeyMessages[0].Text);
        Assert.Matches("^[0-9a-f]{32}$", p.KeyMessages[0].Id);
        Assert.Equal(ReportContentProvenance.ConsultantEntered, p.KeyMessages[0].Provenance);
        Assert.Equal(ReportVerificationStatus.Unverified, p.KeyMessages[0].VerificationStatus);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", p.KeyMessages[1].Id);
        Assert.Contains(p.KeyMessages[1].EvidenceReferences, r => r.Kind == ReportEvidenceKind.ScoringDimension);
        Assert.Contains(p.KeyMessages[1].EvidenceReferences, r => r.QuestionId == "q02");
        Assert.Contains(p.EvidenceReferences!, r => r.QuestionId == "q02");
        Assert.Contains(p.EvidenceReferences!, r => r.Kind == ReportEvidenceKind.ScoringDimension);
        Assert.Single(p.DecisionsRequested!);

        // Explicit clearing of risks and questionnaire evidence; non-questionnaire evidence preserved.
        var html2 = await (await s.Client.GetAsync(s.Url)).Content.ReadAsStringAsync();
        var clear = PresentationForm(s.Ids.AssessmentId, s.Ids.ResponseId, after, html2);
        clear["PresentationInput.TopRisksSubmitted"] = "true";
        clear["PresentationInput.TopRisksText"] = string.Empty;
        clear["PresentationInput.EvidenceSubmitted"] = "true";
        var cleared = await s.Client.PostAsync(s.Url + "?handler=SaveExecutivePresentation", new FormUrlEncodedContent(clear));
        Assert.Contains("Executive Presentation Draft saved.", await cleared.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var c = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!).ExecutivePresentation;
        Assert.True(c.TopRisks is null || c.TopRisks.Count == 0);
        Assert.DoesNotContain(c.EvidenceReferences!, r => r.QuestionId == "q02");
        Assert.Contains(c.EvidenceReferences!, r => r.Kind == ReportEvidenceKind.ScoringDimension);
        // Omitted saved entries are preserved.
        Assert.Equal(2, c.KeyMessages!.Count);
        Assert.Single(c.DecisionsRequested!);
    }

    [Fact]
    public async Task Presentation_Omitted_Fields_Preserve_Stored_Values()
    {
        var s = await PresentationSetupAsync(new ExecutivePresentationSection(ReportAssessmentState.Assessed, "Title", "Narr",
            new[] { PresEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Msg A") }, null, new[] { "Risk old" }, null));
        await using var factory = s.Factory;
        var form = PresentationForm(s.Ids.AssessmentId, s.Ids.ResponseId, s.Revision, s.Html);
        var html = await (await s.Client.PostAsync(s.Url + "?handler=SaveExecutivePresentation", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();
        Assert.Contains("Executive Presentation Draft saved.", html, StringComparison.Ordinal);
        var p = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!).ExecutivePresentation;
        Assert.Equal(new[] { "Risk old" }, p.TopRisks);
        Assert.Equal("Title", p.Title);
        Assert.Single(p.KeyMessages!);
        Assert.Contains(p.KeyMessages![0].EvidenceReferences, r => r.Kind == ReportEvidenceKind.ScoringDimension);
    }

    [Fact]
    public async Task Presentation_Duplicate_Ids_Across_Lists_Are_Rejected_And_Input_Retained()
    {
        var s = await PresentationSetupAsync(new ExecutivePresentationSection(ReportAssessmentState.Assessed, "T", null,
            new[] { PresEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Msg A") }, new[] { PresEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Dec B") }, null, null));
        await using var factory = s.Factory;
        var form = PresentationForm(s.Ids.AssessmentId, s.Ids.ResponseId, s.Revision, s.Html);
        form["PresentationInput.KeyMessages[0].Id"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        form["PresentationInput.KeyMessages[0].Text"] = "Retained duplicate text";
        form["PresentationInput.DecisionsRequested[0].Id"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        form["PresentationInput.DecisionsRequested[0].Text"] = "Dup decision";
        var html = await (await s.Client.PostAsync(s.Url + "?handler=SaveExecutivePresentation", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Executive Presentation Draft saved.", html, StringComparison.Ordinal);
        Assert.Contains("IDs must be unique", html, StringComparison.Ordinal);
        Assert.Contains("Retained duplicate text", html, StringComparison.Ordinal);
        var p = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!).ExecutivePresentation;
        Assert.Equal("Msg A", p.KeyMessages!.Single().Text);
    }

    [Fact]
    public async Task Presentation_NotAssessed_With_Content_Is_Rejected()
    {
        var s = await PresentationSetupAsync(null);
        await using var factory = s.Factory;
        var form = PresentationForm(s.Ids.AssessmentId, s.Ids.ResponseId, s.Revision, s.Html);
        form["PresentationInput.State"] = nameof(ReportAssessmentState.NotAssessed);
        form["PresentationInput.Title"] = "Retained conflict title";
        var html = await (await s.Client.PostAsync(s.Url + "?handler=SaveExecutivePresentation", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Executive Presentation Draft saved.", html, StringComparison.Ordinal);
        Assert.Contains("Retained conflict title", html, StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);
    }

    [Fact]
    public async Task Presentation_Stale_Stamp_And_Missing_Antiforgery_And_Unauthorized_Are_Rejected()
    {
        var s = await PresentationSetupAsync(null);
        await using var factory = s.Factory;
        var form = PresentationForm(s.Ids.AssessmentId, s.Ids.ResponseId, s.Revision, s.Html);
        form["ExpectedConcurrencyStamp"] = Guid.NewGuid().ToString();
        form["PresentationInput.Title"] = "Stale title";
        var html = await (await s.Client.PostAsync(s.Url + "?handler=SaveExecutivePresentation", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();
        Assert.Contains("changed after you opened it", html, StringComparison.Ordinal);
        Assert.Contains("Stale title", html, StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);

        var noToken = await s.Client.PostAsync(s.Url + "?handler=SaveExecutivePresentation", new FormUrlEncodedContent(new Dictionary<string, string> { ["PresentationInput.State"] = "Assessed" }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        var regular = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        regular.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "user");
        Assert.Equal(HttpStatusCode.Forbidden, (await regular.PostAsync(s.Url + "?handler=SaveExecutivePresentation", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
    }

    [Fact]
    public async Task Presentation_NonDraft_Is_Rejected()
    {
        var s = await PresentationSetupAsync(null);
        await using var factory = s.Factory;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Set<AssessmentReportRevision>().SingleAsync(item => item.Id == s.Revision.Id);
            stored.Status = AssessmentReportRevisionStatus.Approved;
            await db.SaveChangesAsync();
        }
        var form = PresentationForm(s.Ids.AssessmentId, s.Ids.ResponseId, s.Revision, s.Html);
        form["PresentationInput.Title"] = "Keep non-draft title";
        var html = await (await s.Client.PostAsync(s.Url + "?handler=SaveExecutivePresentation", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();
        Assert.Contains("Only Draft report revisions can be edited", html, StringComparison.Ordinal);
        Assert.Contains("Keep non-draft title", html, StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);
    }

    [Fact]
    public async Task Presentation_Editor_Renders_Remove_Only_For_Unsaved_Entries_And_Indexed_Names()
    {
        var s = await PresentationSetupAsync(new ExecutivePresentationSection(ReportAssessmentState.Assessed, "T", null,
            new[] { PresEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Msg A"), PresEntry("cccccccccccccccccccccccccccccccc", "Msg C") }, null, null, null));
        await using var factory = s.Factory;
        var html = s.Html;
        Assert.Contains("id=\"executivePresentation\"", html, StringComparison.Ordinal);
        var templateStart = html.IndexOf("<template id=\"presKeyMessageTemplate\"", StringComparison.Ordinal);
        Assert.True(templateStart > 0);
        var listStart = html.IndexOf("id=\"presKeyMessagesList\"", StringComparison.Ordinal);
        var saved = html[listStart..templateStart];
        var template = html[templateStart..html.IndexOf("</template>", templateStart, StringComparison.Ordinal)];
        Assert.DoesNotContain("Remove this unsaved entry", saved.Split("id=\"addPresKeyMessage\"")[0], StringComparison.Ordinal);
        Assert.Contains("PresentationInput.KeyMessages[0].Text", saved, StringComparison.Ordinal);
        Assert.Contains("PresentationInput.KeyMessages[1].Text", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("PresentationInput.KeyMessages[2]", saved.Split("id=\"addPresKeyMessage\"")[0], StringComparison.Ordinal);
        Assert.Contains("Remove this unsaved entry", template, StringComparison.Ordinal);
        Assert.Contains("PresentationInput.KeyMessages[__index__].Text", template, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> RoadmapForm(Guid assessmentId, Guid responseId, AssessmentReportRevision revision, string html) => new()
    {
        ["__RequestVerificationToken"] = ExtractToken(html),
        ["AssessmentId"] = assessmentId.ToString(),
        ["SubmittedResponseId"] = responseId.ToString(),
        ["RevisionId"] = revision.Id.ToString(),
        ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
        ["RoadmapInput.State"] = nameof(ReportAssessmentState.Assessed)
    };

    private static RoadmapItem Road(string id, int priority, int start, int end, params string[] deps) =>
        new(id, "Item " + id, "Do work", start, end, priority, "Owner", deps, new[] { "Done" }, new[] { "Assume" }, "Measure",
            ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.Unverified,
            new[] { new ReportEvidenceReference(ReportEvidenceKind.ScoringDimension, "D1") });

    [Fact]
    public async Task Roadmap_Create_Edit_Dependencies_Preserve_Other_Data()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var a = Road("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 2, 1, 5);
        var b = Road("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 1, 6, 9);
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(ReportAssessmentState.Assessed, "Keep summary"),
            ImplementationRoadmap = new ImplementationRoadmapSection(ReportAssessmentState.Assessed, new[] { a, b })
        });
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var snapshot = revision.ScoringSnapshot;
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        Assert.Contains("Remove this unsaved roadmap item", html + "<template>Remove this unsaved roadmap item", StringComparison.Ordinal);
        Assert.True(html.IndexOf("Item bbbb", StringComparison.Ordinal) < html.IndexOf("Item aaaa", StringComparison.Ordinal) || html.Contains("value=\"Item bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\""));

        // Items sorted by priority: [0]=b (priority 1), [1]=a (priority 2). Omit dependency fields to preserve; add new item.
        var form = RoadmapForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["RoadmapInput.Items[0].Id"] = b.Id;
        form["RoadmapInput.Items[0].Title"] = "Edited <b>b</b>";
        form["RoadmapInput.Items[0].DependenciesSubmitted"] = "true";
        form["RoadmapInput.Items[0].DependencyIds"] = a.Id;
        form["RoadmapInput.Items[0].AcceptanceCriteriaText"] = "One\r\n\r\nTwo";
        form["RoadmapInput.Items[0].EvidenceQuestionIds"] = "q02";
        form["RoadmapInput.Items[1].Id"] = a.Id;
        form["RoadmapInput.Items[2].Id"] = string.Empty;
        form["RoadmapInput.Items[2].Title"] = "New item";
        form["RoadmapInput.Items[2].Activities"] = "New work";
        form["RoadmapInput.Items[2].StartDay"] = "1";
        form["RoadmapInput.Items[2].EndDay"] = "90";
        var response = await client.PostAsync(url + "?handler=SaveRoadmap", new FormUrlEncodedContent(form));
        var resultHtml = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Implementation Roadmap Draft saved.", resultHtml, StringComparison.Ordinal);
        Assert.Contains("Edited &lt;b&gt;b&lt;/b&gt;", resultHtml, StringComparison.Ordinal);

        var after = await factory.GetOnlyReportAsync();
        var saved = ConsultantReportContentSerializer.Deserialize(after.Revisions.Single().ConsultantContentJson!);
        Assert.Equal("Keep summary", saved.ExecutiveSummary.Narrative);
        Assert.Equal(snapshot, after.Revisions.Single().ScoringSnapshot);
        var items = saved.ImplementationRoadmap.Items!;
        Assert.Equal(3, items.Count);
        var editedB = items.Single(i => i.Id == b.Id);
        Assert.Equal(new[] { a.Id }, editedB.Dependencies);
        Assert.Equal(new[] { "One", "Two" }, editedB.AcceptanceCriteria);
        Assert.Equal(1, editedB.Priority);
        Assert.Contains(editedB.EvidenceReferences, r => r.Kind == ReportEvidenceKind.ScoringDimension);
        Assert.Contains(editedB.EvidenceReferences, r => r.QuestionId == "q02");
        var created = items.Single(i => i.Id != a.Id && i.Id != b.Id);
        Assert.Matches("^[0-9a-f]{32}$", created.Id);
        Assert.Equal(3, created.Priority);
        Assert.Equal(ReportContentProvenance.ConsultantEntered, created.Provenance);
        Assert.Equal(ReportVerificationStatus.Unverified, created.VerificationStatus);
        Assert.Equal(90, created.EndDay);
        Assert.Equal(a.Title, items.Single(i => i.Id == a.Id).Title);
    }

    [Fact]
    public async Task Roadmap_Invalid_Dependency_And_Duplicate_Priority_Are_Rejected_And_Input_Retained()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var a = Road("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1, 1, 5);
        var b = Road("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 2, 6, 9);
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            ImplementationRoadmap = new ImplementationRoadmapSection(ReportAssessmentState.Assessed, new[] { a, b })
        });
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        async Task<string> Post(Action<Dictionary<string, string>> edit)
        {
            var form = RoadmapForm(ids.AssessmentId, ids.ResponseId, revision, html);
            form["RoadmapInput.Items[0].Id"] = a.Id;
            form["RoadmapInput.Items[1].Id"] = b.Id;
            edit(form);
            var r = await client.PostAsync(url + "?handler=SaveRoadmap", new FormUrlEncodedContent(form));
            return await r.Content.ReadAsStringAsync();
        }

        var self = await Post(f => { f["RoadmapInput.Items[0].DependenciesSubmitted"] = "true"; f["RoadmapInput.Items[0].DependencyIds"] = a.Id; f["RoadmapInput.Items[0].Title"] = "Retained <x>"; });
        Assert.Contains("cannot depend on itself", self, StringComparison.Ordinal);
        Assert.Contains("Retained &lt;x&gt;", self, StringComparison.Ordinal);
        var unknown = await Post(f => { f["RoadmapInput.Items[0].DependenciesSubmitted"] = "true"; f["RoadmapInput.Items[0].DependencyIds"] = "cccccccccccccccccccccccccccccccc"; });
        Assert.Contains("saved roadmap items", unknown, StringComparison.Ordinal);
        var ordering = await Post(f => { f["RoadmapInput.Items[0].DependenciesSubmitted"] = "true"; f["RoadmapInput.Items[0].DependencyIds"] = b.Id; });
        Assert.DoesNotContain("Implementation Roadmap Draft saved.", ordering, StringComparison.Ordinal);
        var duplicate = await Post(f => { f["RoadmapInput.Items[1].Priority"] = "1"; });
        Assert.DoesNotContain("Implementation Roadmap Draft saved.", duplicate, StringComparison.Ordinal);
        var badDay = await Post(f => { f["RoadmapInput.Items[0].EndDay"] = "91"; });
        Assert.DoesNotContain("Implementation Roadmap Draft saved.", badDay, StringComparison.Ordinal);

        var saved = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        Assert.Equal(2, saved.ImplementationRoadmap.Items!.Count);
        Assert.All(saved.ImplementationRoadmap.Items!, i => Assert.Empty(i.Dependencies));
    }

    [Fact]
    public async Task Roadmap_Empty_Dependency_Submission_Clears_And_Omission_Preserves()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var a = Road("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1, 1, 5);
        var b = Road("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 2, 6, 9, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            ImplementationRoadmap = new ImplementationRoadmapSection(ReportAssessmentState.Assessed, new[] { a, b })
        });
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        var omit = RoadmapForm(ids.AssessmentId, ids.ResponseId, revision, html);
        omit["RoadmapInput.Items[0].Id"] = b.Id;
        omit["RoadmapInput.Items[0].Title"] = "Still b";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(url + "?handler=SaveRoadmap", new FormUrlEncodedContent(omit))).StatusCode);
        var saved = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        Assert.Equal(new[] { a.Id }, saved.ImplementationRoadmap.Items!.Single(i => i.Id == b.Id).Dependencies);

        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var clear = RoadmapForm(ids.AssessmentId, ids.ResponseId, revision, html);
        clear["RoadmapInput.Items[0].Id"] = b.Id;
        clear["RoadmapInput.Items[0].DependenciesSubmitted"] = "true";
        await client.PostAsync(url + "?handler=SaveRoadmap", new FormUrlEncodedContent(clear));
        saved = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        Assert.Empty(saved.ImplementationRoadmap.Items!.Single(i => i.Id == b.Id).Dependencies);
    }

    [Fact]
    public async Task Roadmap_Stale_Stamp_Is_Rejected_Without_Saving()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var form = RoadmapForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["ExpectedConcurrencyStamp"] = Guid.NewGuid().ToString();
        form["RoadmapInput.Items[0].Title"] = "Stale";
        form["RoadmapInput.Items[0].Activities"] = "Work";
        var result = await (await client.PostAsync(url + "?handler=SaveRoadmap", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Implementation Roadmap Draft saved.", result, StringComparison.Ordinal);
        Assert.Contains("Stale", result, StringComparison.Ordinal);
        var json = (await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson;
        Assert.True(string.IsNullOrWhiteSpace(json) || ConsultantReportContentSerializer.Deserialize(json).ImplementationRoadmap.Items is not { Count: > 0 });
    }

    [Fact]
    public async Task Roadmap_Post_Without_Antiforgery_Token_Is_Rejected()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var response = await client.PostAsync(url + "?handler=SaveRoadmap", new FormUrlEncodedContent(new Dictionary<string, string> { ["RoadmapInput.State"] = "Assessed" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Roadmap_Save_Rejects_Unauthorized_And_NonDraft_Requests()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var regular = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        regular.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "user");
        Assert.Equal(HttpStatusCode.Forbidden, (await regular.PostAsync(url + "?handler=SaveRoadmap", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);

        var client = factory.CreateAdminClient();
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Set<AssessmentReportRevision>().SingleAsync(item => item.Id == revision.Id);
            stored.Status = AssessmentReportRevisionStatus.Approved;
            await db.SaveChangesAsync();
        }
        var form = RoadmapForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["RoadmapInput.Items[0].Title"] = "Keep non-draft roadmap input";
        var response = await client.PostAsync(url + "?handler=SaveRoadmap", new FormUrlEncodedContent(form));
        var resultHtml = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Only Draft report revisions can be edited", resultHtml, StringComparison.Ordinal);
        Assert.Contains("Keep non-draft roadmap input", resultHtml, StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);
    }

    [Fact]
    public async Task Roadmap_New_Items_Default_Priority_When_Omitted_Including_Existing_Gaps()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var a = Road("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1, 1, 5);
        var b = Road("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 3, 6, 9);
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            ImplementationRoadmap = new ImplementationRoadmapSection(ReportAssessmentState.Assessed, new[] { a, b })
        });
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        var form = RoadmapForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["RoadmapInput.Items[0].Id"] = a.Id;
        form["RoadmapInput.Items[1].Id"] = b.Id;
        form["RoadmapInput.Items[2].Id"] = string.Empty;
        form["RoadmapInput.Items[2].Title"] = "First new";
        form["RoadmapInput.Items[2].Activities"] = "Work";
        form["RoadmapInput.Items[2].StartDay"] = "10";
        form["RoadmapInput.Items[2].EndDay"] = "12";
        form["RoadmapInput.Items[3].Id"] = string.Empty;
        form["RoadmapInput.Items[3].Title"] = "Second new";
        form["RoadmapInput.Items[3].Activities"] = "Work";
        form["RoadmapInput.Items[3].StartDay"] = "13";
        form["RoadmapInput.Items[3].EndDay"] = "15";
        var resultHtml = await (await client.PostAsync(url + "?handler=SaveRoadmap", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();
        Assert.Contains("Implementation Roadmap Draft saved.", resultHtml, StringComparison.Ordinal);

        var items = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!).ImplementationRoadmap.Items!;
        Assert.Equal(4, items.Count);
        Assert.Equal(1, items.Single(i => i.Id == a.Id).Priority);
        Assert.Equal(3, items.Single(i => i.Id == b.Id).Priority);
        Assert.Equal(2, items.Single(i => i.Title == "First new").Priority);
        Assert.Equal(4, items.Single(i => i.Title == "Second new").Priority);
        Assert.Equal(4, items.Select(i => i.Priority).Distinct().Count());
    }

    [Theory]
    [InlineData(1, 90, true)]
    [InlineData(1, 1, true)]
    [InlineData(90, 90, true)]
    [InlineData(0, 10, false)]
    [InlineData(1, 91, false)]
    [InlineData(50, 49, false)]
    public async Task Roadmap_Day_Boundaries_Are_Inclusive_For_1_And_90(int startDay, int endDay, bool accepted)
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var form = RoadmapForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["RoadmapInput.Items[0].Id"] = string.Empty;
        form["RoadmapInput.Items[0].Title"] = "Boundary item";
        form["RoadmapInput.Items[0].Activities"] = "Work";
        form["RoadmapInput.Items[0].StartDay"] = startDay.ToString();
        form["RoadmapInput.Items[0].EndDay"] = endDay.ToString();
        var resultHtml = await (await client.PostAsync(url + "?handler=SaveRoadmap", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();

        var json = (await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson;
        if (accepted)
        {
            Assert.Contains("Implementation Roadmap Draft saved.", resultHtml, StringComparison.Ordinal);
            var item = ConsultantReportContentSerializer.Deserialize(json!).ImplementationRoadmap.Items!.Single();
            Assert.Equal(startDay, item.StartDay);
            Assert.Equal(endDay, item.EndDay);
        }
        else
        {
            Assert.DoesNotContain("Implementation Roadmap Draft saved.", resultHtml, StringComparison.Ordinal);
            Assert.Contains("Boundary item", resultHtml, StringComparison.Ordinal);
            Assert.True(string.IsNullOrWhiteSpace(json) || ConsultantReportContentSerializer.Deserialize(json).ImplementationRoadmap.Items is not { Count: > 0 });
        }
    }

    [Fact]
    public async Task Roadmap_NotAssessed_With_Items_Is_Rejected_And_Input_Retained()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var a = Road("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1, 1, 5);
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            ImplementationRoadmap = new ImplementationRoadmapSection(ReportAssessmentState.Assessed, new[] { a })
        });
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var form = RoadmapForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["RoadmapInput.State"] = nameof(ReportAssessmentState.NotAssessed);
        form["RoadmapInput.Items[0].Id"] = a.Id;
        form["RoadmapInput.Items[0].Title"] = "Retained posted title";
        var resultHtml = await (await client.PostAsync(url + "?handler=SaveRoadmap", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();

        Assert.DoesNotContain("Implementation Roadmap Draft saved.", resultHtml, StringComparison.Ordinal);
        Assert.Contains("Retained posted title", resultHtml, StringComparison.Ordinal);
        var saved = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        Assert.Equal(ReportAssessmentState.Assessed, saved.ImplementationRoadmap.State);
        Assert.Equal(a.Title, saved.ImplementationRoadmap.Items!.Single().Title);
    }

    [Fact]
    public async Task Roadmap_Editor_Renders_Remove_Only_For_Unsaved_Rows_And_Indexed_Field_Names()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            ImplementationRoadmap = new ImplementationRoadmapSection(ReportAssessmentState.Assessed,
                new[] { Road("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1, 1, 5), Road("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 2, 6, 9) })
        });
        var client = factory.CreateAdminClient();
        var html = await (await client.GetAsync(factory.PageUrl(ids.AssessmentId, ids.ResponseId))).Content.ReadAsStringAsync();

        var templateStart = html.IndexOf("<template id=\"roadmapItemTemplate\"", StringComparison.Ordinal);
        Assert.True(templateStart > 0);
        var templateEnd = html.IndexOf("</template>", templateStart, StringComparison.Ordinal);
        var rendered = html[..templateStart];
        var template = html[templateStart..templateEnd];
        var roadmapStart = rendered.IndexOf("id=\"roadmapList\"", StringComparison.Ordinal);
        Assert.True(roadmapStart > 0);
        var saved = rendered[roadmapStart..];

        Assert.DoesNotContain("remove-new-row", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove this unsaved roadmap item", saved, StringComparison.Ordinal);
        foreach (var field in new[] { "Id", "Title", "Activities", "StartDay", "EndDay", "Priority" })
        {
            Assert.Contains($"name=\"RoadmapInput.Items[0].{field}\"", saved, StringComparison.Ordinal);
            Assert.Contains($"name=\"RoadmapInput.Items[1].{field}\"", saved, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("RoadmapInput.Items[2]", saved, StringComparison.Ordinal);

        Assert.Contains("remove-new-row", template, StringComparison.Ordinal);
        Assert.Contains("Remove this unsaved roadmap item", template, StringComparison.Ordinal);
        Assert.Contains("name=\"RoadmapInput.Items[__index__].Title\"", template, StringComparison.Ordinal);
        Assert.Contains("name=\"RoadmapInput.Items[__index__].Priority\"", template, StringComparison.Ordinal);
        Assert.DoesNotContain("DependenciesSubmitted", template, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> TechForm(Guid assessmentId, Guid responseId, AssessmentReportRevision revision, string html) => new()
    {
        ["__RequestVerificationToken"] = ExtractToken(html),
        ["AssessmentId"] = assessmentId.ToString(),
        ["SubmittedResponseId"] = responseId.ToString(),
        ["RevisionId"] = revision.Id.ToString(),
        ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
        ["TechnicalInput.State"] = nameof(ReportAssessmentState.Assessed)
    };

    [Fact]
    public async Task TechnicalGaps_Create_Edit_Reload_Preserve_Ids_Evidence_And_Other_Sections()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var keptGap = new TechnicalGapEntry("11111111111111111111111111111111", "Kept gap", "Desc", "Impact",
            ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.Unverified,
            new[] { new ReportEvidenceReference(ReportEvidenceKind.ScoringDimension, "D1") }, true);
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(ReportAssessmentState.Assessed, "Keep summary"),
            TechnicalGapsAndArchitecture = new TechnicalGapsAndArchitectureSection(ReportAssessmentState.Assessed, new[] { keptGap })
        });
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var snapshot = revision.ScoringSnapshot;
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        Assert.Contains("Remove unsaved gap", html, StringComparison.Ordinal);
        var form = TechForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["TechnicalInput.Gaps[0].Id"] = keptGap.Id;
        form["TechnicalInput.Gaps[0].Title"] = "Edited <b>gap</b>";
        form["TechnicalInput.Gaps[0].EvidenceQuestionIds"] = "q02";
        form["TechnicalInput.Gaps[1].Id"] = string.Empty;
        form["TechnicalInput.Gaps[1].Title"] = "New gap";
        form["TechnicalInput.Gaps[1].Description"] = "New desc";
        form["TechnicalInput.Gaps[1].Impact"] = "New impact";
        form["TechnicalInput.Recommendations[0].Id"] = string.Empty;
        form["TechnicalInput.Recommendations[0].Title"] = "Rec";
        form["TechnicalInput.Recommendations[0].Recommendation"] = "Do it";
        form["TechnicalInput.Recommendations[0].Rationale"] = "Because";
        form["TechnicalInput.Recommendations[0].RequiresValidation"] = "true";
        form["TechnicalInput.Recommendations[0].EvidenceQuestionIds"] = "q02";

        var response = await client.PostAsync(url + "?handler=SaveTechnicalGaps", new FormUrlEncodedContent(form));
        var resultHtml = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Edited &lt;b&gt;gap&lt;/b&gt;", resultHtml, StringComparison.Ordinal);
        Assert.Contains("Technical Gaps &amp; Architecture Draft saved.", resultHtml, StringComparison.Ordinal);

        var saved = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        Assert.Equal("Keep summary", saved.ExecutiveSummary.Narrative);
        Assert.Equal(snapshot, (await factory.GetOnlyReportAsync()).Revisions.Single().ScoringSnapshot);
        var gaps = saved.TechnicalGapsAndArchitecture.Gaps!;
        Assert.Equal(2, gaps.Count);
        var edited = gaps.Single(g => g.Id == keptGap.Id);
        Assert.Equal("Edited <b>gap</b>", edited.Title);
        Assert.True(edited.RequiresValidation);
        Assert.Contains(edited.EvidenceReferences, r => r.Kind == ReportEvidenceKind.ScoringDimension);
        Assert.Contains(edited.EvidenceReferences, r => r.QuestionId == "q02");
        var created = gaps.Single(g => g.Id != keptGap.Id);
        Assert.Equal(ReportContentProvenance.ConsultantEntered, created.Provenance);
        Assert.Equal(ReportVerificationStatus.Unverified, created.VerificationStatus);
        Assert.False(created.RequiresValidation);
        var rec = Assert.Single(saved.TechnicalGapsAndArchitecture.Recommendations!);
        Assert.True(rec.RequiresValidation);
        Assert.Equal("q02", Assert.Single(rec.EvidenceReferences).QuestionId);

        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var reloaded = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var omit = TechForm(ids.AssessmentId, ids.ResponseId, revision, reloaded);
        omit["TechnicalInput.Gaps[0].Id"] = created.Id;
        var omitted = await client.PostAsync(url + "?handler=SaveTechnicalGaps", new FormUrlEncodedContent(omit));
        Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
        var after = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        Assert.Equal(2, after.TechnicalGapsAndArchitecture.Gaps!.Count);
        Assert.Equal("New gap", after.TechnicalGapsAndArchitecture.Gaps!.Single(g => g.Id == created.Id).Title);
        Assert.Single(after.TechnicalGapsAndArchitecture.Recommendations!);
    }

    [Fact]
    public async Task TechnicalGaps_Reject_Invalid_Ids_Evidence_Stale_NonDraft_And_Unauthorized()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var form = TechForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["TechnicalInput.Gaps[0].Id"] = "not-a-known-id";
        form["TechnicalInput.Gaps[0].Title"] = "Retained gap";
        var badId = await client.PostAsync(url + "?handler=SaveTechnicalGaps", new FormUrlEncodedContent(form));
        var badIdHtml = await badId.Content.ReadAsStringAsync();
        Assert.Contains("ID is invalid", badIdHtml, StringComparison.Ordinal);
        Assert.Contains("Retained gap", badIdHtml, StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);

        var dupe = Guid.NewGuid().ToString("N");
        form["TechnicalInput.Gaps[0].Id"] = dupe;
        form["TechnicalInput.Gaps[1].Id"] = dupe;
        var dupResponse = await client.PostAsync(url + "?handler=SaveTechnicalGaps", new FormUrlEncodedContent(form));
        Assert.Contains("IDs must be unique", await dupResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        form.Remove("TechnicalInput.Gaps[1].Id");
        form["TechnicalInput.Gaps[0].Id"] = string.Empty;
        form["TechnicalInput.Gaps[0].EvidenceQuestionIds"] = "q-unknown";
        var badEvidence = await client.PostAsync(url + "?handler=SaveTechnicalGaps", new FormUrlEncodedContent(form));
        Assert.Contains("Retained gap", await badEvidence.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);

        form.Remove("TechnicalInput.Gaps[0].EvidenceQuestionIds");
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty());
        var stale = await client.PostAsync(url + "?handler=SaveTechnicalGaps", new FormUrlEncodedContent(form));
        Assert.Contains("changed after you opened it", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url + "?handler=SaveTechnicalGaps", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        var regular = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        regular.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "user");
        Assert.Equal(HttpStatusCode.Forbidden, (await regular.PostAsync(url + "?handler=SaveTechnicalGaps", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Set<AssessmentReportRevision>().SingleAsync(item => item.Id == revision.Id);
            stored.Status = AssessmentReportRevisionStatus.Approved;
            await db.SaveChangesAsync();
        }
        var fresh = (await factory.GetOnlyReportAsync()).Revisions.Single();
        form["ExpectedConcurrencyStamp"] = fresh.ConcurrencyStamp.ToString();
        var nonDraft = await client.PostAsync(url + "?handler=SaveTechnicalGaps", new FormUrlEncodedContent(form));
        Assert.Contains("Only Draft report revisions can be edited", await nonDraft.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TechnicalGaps_NotAssessed_With_Content_Is_Rejected_And_Input_Retained()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var form = TechForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["TechnicalInput.State"] = nameof(ReportAssessmentState.NotAssessed);
        form["TechnicalInput.Gaps[0].Id"] = string.Empty;
        form["TechnicalInput.Gaps[0].Title"] = "Retained conflicting gap";
        form["TechnicalInput.Recommendations[0].Id"] = string.Empty;
        form["TechnicalInput.Recommendations[0].Title"] = "Retained conflicting recommendation";

        var response = await client.PostAsync(url + "?handler=SaveTechnicalGaps", new FormUrlEncodedContent(form));
        var resultHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("must not contain assessed content", resultHtml, StringComparison.Ordinal);
        Assert.Contains("Retained conflicting gap", resultHtml, StringComparison.Ordinal);
        Assert.Contains("Retained conflicting recommendation", resultHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Technical Gaps &amp; Architecture Draft saved.", resultHtml, StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);
    }

    [Fact]
    public async Task TechnicalGaps_Recommendation_Preserves_NonQuestionnaire_Evidence()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var preserved = new ReportEvidenceReference(ReportEvidenceKind.ScoringDimension, "D1");
        var existing = new ArchitectureRecommendation("22222222222222222222222222222222", "Rec", "Do it", "Because",
            ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.Unverified, new[] { preserved });
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            TechnicalGapsAndArchitecture = new TechnicalGapsAndArchitectureSection(ReportAssessmentState.Assessed, null, new[] { existing })
        });
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        Assert.Contains("Other preserved evidence references (read-only): ScoringDimension: D1", html, StringComparison.Ordinal);
        var form = TechForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["TechnicalInput.Recommendations[0].Id"] = existing.Id;
        form["TechnicalInput.Recommendations[0].EvidenceQuestionIds"] = "q02";

        var response = await client.PostAsync(url + "?handler=SaveTechnicalGaps", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        var rec = Assert.Single(saved.TechnicalGapsAndArchitecture.Recommendations!);
        Assert.Equal(existing.Id, rec.Id);
        Assert.Equal("Do it", rec.Recommendation);
        Assert.Contains(rec.EvidenceReferences, r => r.Kind == ReportEvidenceKind.ScoringDimension && r.ReferenceId == "D1");
        Assert.Contains(rec.EvidenceReferences, r => r.QuestionId == "q02");
    }

    private static Dictionary<string, string> FindingForm(Guid assessmentId, Guid responseId, AssessmentReportRevision revision, string html) => new()
    {
        ["__RequestVerificationToken"] = ExtractToken(html),
        ["AssessmentId"] = assessmentId.ToString(),
        ["SubmittedResponseId"] = responseId.ToString(),
        ["RevisionId"] = revision.Id.ToString(),
        ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
        ["IndustrialInput.State"] = nameof(ReportAssessmentState.Assessed)
    };

    [Fact]
    public async Task IndustrialFindings_Create_Edit_Reload_Preserve_Id_And_Other_Content()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var system = new SystemAssessmentEntry("sys-keep", "Keep", ReportAssessmentState.Assessed, null, null, null, null, null, null,
            ReportContentProvenance.QuestionnaireDerived, ReportVerificationStatus.SelfReported, Array.Empty<ReportEvidenceReference>());
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(ReportAssessmentState.Assessed, "Keep summary"),
            IndustrialDataAndSystems = new IndustrialDataAndSystemsSection(ReportAssessmentState.Assessed, new[] { system })
        });
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var snapshot = revision.ScoringSnapshot;
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var form = FindingForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["IndustrialInput.Systems[0].Id"] = "sys-keep";
        form["IndustrialInput.Systems[0].Name"] = "Keep";
        form["IndustrialInput.Systems[0].AssessmentState"] = nameof(ReportAssessmentState.Assessed);
        form["IndustrialInput.Findings[0].Id"] = string.Empty;
        form["IndustrialInput.Findings[0].Title"] = "Gap <b>one</b>";
        form["IndustrialInput.Findings[0].Narrative"] = "Narrative";
        form["IndustrialInput.Findings[0].Classification"] = nameof(ReportFindingClassification.PreliminaryFinding);
        form["IndustrialInput.Findings[0].Provenance"] = nameof(ReportContentProvenance.ConsultantEntered);
        form["IndustrialInput.Findings[0].VerificationStatus"] = nameof(ReportVerificationStatus.Unverified);
        form["IndustrialInput.Findings[0].ConsultantNotes"] = "Notes";

        var first = await client.PostAsync(url + "?handler=SaveIndustrialData", new FormUrlEncodedContent(form));
        var firstHtml = await first.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("Gap &lt;b&gt;one&lt;/b&gt;", firstHtml, StringComparison.Ordinal);
        var saved = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        var finding = Assert.Single(saved.IndustrialDataAndSystems.Findings!);
        Assert.True(Guid.TryParseExact(finding.Id, "N", out _));
        Assert.Empty(finding.EvidenceReferences);
        Assert.Equal("Keep summary", saved.ExecutiveSummary.Narrative);
        Assert.Equal("sys-keep", saved.IndustrialDataAndSystems.Systems!.Single().Id);
        Assert.Equal(ReportVerificationStatus.SelfReported, saved.IndustrialDataAndSystems.Systems!.Single().VerificationStatus);
        Assert.Equal(snapshot, (await factory.GetOnlyReportAsync()).Revisions.Single().ScoringSnapshot);

        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var reloaded = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        Assert.Contains(finding.Id, reloaded, StringComparison.Ordinal);
        var edit = FindingForm(ids.AssessmentId, ids.ResponseId, revision, reloaded);
        foreach (var pair in form.Where(p => p.Key.StartsWith("IndustrialInput.")))
        {
            edit[pair.Key] = pair.Value;
        }
        edit["IndustrialInput.Findings[0].Id"] = finding.Id;
        edit["IndustrialInput.Findings[0].Title"] = "Edited";
        edit["IndustrialInput.Findings[0].EvidenceQuestionIds"] = "q02";
        edit["IndustrialInput.EvidenceQuestionIds"] = "q11";
        var second = await client.PostAsync(url + "?handler=SaveIndustrialData", new FormUrlEncodedContent(edit));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var savedAgain = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        var edited = Assert.Single(savedAgain.IndustrialDataAndSystems.Findings!);
        Assert.Equal(finding.Id, edited.Id);
        Assert.Equal("Edited", edited.Title);
        Assert.Equal("q02", Assert.Single(edited.EvidenceReferences).QuestionId);
        Assert.Equal("q11", Assert.Single(savedAgain.IndustrialDataAndSystems.EvidenceReferences!).QuestionId);
    }

    [Fact]
    public async Task IndustrialFindings_Reject_Invalid_Evidence_And_Stale_Stamp_And_Retain_Input()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var form = FindingForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["IndustrialInput.Findings[0].Id"] = string.Empty;
        form["IndustrialInput.Findings[0].Title"] = "Retained title";
        form["IndustrialInput.Findings[0].Narrative"] = "Retained narrative";
        form["IndustrialInput.Findings[0].Classification"] = nameof(ReportFindingClassification.Finding);
        form["IndustrialInput.Findings[0].Provenance"] = nameof(ReportContentProvenance.ConsultantEntered);
        form["IndustrialInput.Findings[0].VerificationStatus"] = nameof(ReportVerificationStatus.Unverified);
        form["IndustrialInput.EvidenceQuestionIds"] = "q-unknown";

        var invalid = await client.PostAsync(url + "?handler=SaveIndustrialData", new FormUrlEncodedContent(form));
        var invalidHtml = await invalid.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("Retained title", invalidHtml, StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);

        form.Remove("IndustrialInput.EvidenceQuestionIds");
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty());
        var stale = await client.PostAsync(url + "?handler=SaveIndustrialData", new FormUrlEncodedContent(form));
        var staleHtml = await stale.Content.ReadAsStringAsync();
        Assert.Contains("changed after you opened it", staleHtml, StringComparison.Ordinal);
        Assert.Contains("Retained title", staleHtml, StringComparison.Ordinal);
        var persisted = ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!);
        Assert.Null(persisted.IndustrialDataAndSystems.Findings);
    }

    [Fact]
    public async Task IndustrialFindings_Draft_Warns_On_Unverified_Finding_And_Saves()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var form = FindingForm(ids.AssessmentId, ids.ResponseId, revision, html);
        form["IndustrialInput.Findings[0].Id"] = string.Empty;
        form["IndustrialInput.Findings[0].Title"] = "Draft finding";
        form["IndustrialInput.Findings[0].Narrative"] = "Text";
        form["IndustrialInput.Findings[0].Classification"] = nameof(ReportFindingClassification.Finding);
        form["IndustrialInput.Findings[0].Provenance"] = nameof(ReportContentProvenance.ConsultantEntered);
        form["IndustrialInput.Findings[0].VerificationStatus"] = nameof(ReportVerificationStatus.Unverified);
        var response = await client.PostAsync(url + "?handler=SaveIndustrialData", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Industrial Data &amp; Systems Draft saved.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control").Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IndustrialFindings_Save_Requires_Antiforgery_And_Authorization()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId) + "?handler=SaveIndustrialData";
        var admin = factory.CreateAdminClient();
        var noToken = await admin.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["IndustrialInput.Findings[0].Title"] = "x"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        var regular = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        regular.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "user");
        Assert.Equal(HttpStatusCode.Forbidden, (await regular.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);
    }

    [Fact]
    public async Task ExecutiveSummary_Save_Rejects_NonDraft_Revision_And_Retains_Input()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Set<AssessmentReportRevision>().SingleAsync(item => item.Id == revision.Id);
            stored.Status = AssessmentReportRevisionStatus.Approved;
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync(url + "?handler=SaveExecutiveSummary", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(html),
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString(),
            ["RevisionId"] = revision.Id.ToString(),
            ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
            ["Input.State"] = nameof(ReportAssessmentState.NotAssessed),
            ["Input.Narrative"] = "Keep non-Draft text."
        }));
        var resultHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Only Draft report revisions can be edited", resultHtml, StringComparison.Ordinal);
        Assert.Contains("Keep non-Draft text.", resultHtml, StringComparison.Ordinal);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);
    }

    [Fact]
    public async Task ExecutiveSummary_Save_Shows_Draft_Warning()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var unsupported = ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(
                ReportAssessmentState.Assessed,
                Findings: new[] { new ReportFinding("finding-1", "Unverified", "Draft statement", ReportFindingClassification.Finding,
                    ReportContentProvenance.ConsultantEntered, ReportVerificationStatus.Unverified, Array.Empty<ReportEvidenceReference>()) })
        };
        await factory.SetContentAsync(revision.Id, unsupported);
        revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        var response = await client.PostAsync(url + "?handler=SaveExecutiveSummary", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(html),
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString(),
            ["RevisionId"] = revision.Id.ToString(),
            ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
            ["Input.State"] = nameof(ReportAssessmentState.Assessed),
            ["Input.Narrative"] = "Draft warning remains visible."
        }));
        var resultHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Warning:", resultHtml, StringComparison.Ordinal);
        Assert.Contains("factual finding must be verified", resultHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutiveSummary_Save_Stale_Stamp_Preserves_Posted_Text_Without_Overwriting()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var client = factory.CreateAdminClient();
        var url = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        await factory.SetContentAsync(revision.Id, ConsultantReportContent.CreateEmpty() with
        {
            ExecutiveSummary = new ExecutiveSummarySection(ReportAssessmentState.Assessed, "Newer saved content")
        });

        var response = await client.PostAsync(url + "?handler=SaveExecutiveSummary", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(html),
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString(),
            ["RevisionId"] = revision.Id.ToString(),
            ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
            ["Input.State"] = nameof(ReportAssessmentState.Assessed),
            ["Input.Narrative"] = "My unsaved text must remain."
        }));
        var resultHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("changed after you opened it", resultHtml, StringComparison.Ordinal);
        Assert.Contains("My unsaved text must remain.", resultHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.Narrative\" class=\"form-control bg-dark text-light border-secondary\">Newer saved content", resultHtml, StringComparison.Ordinal);
        Assert.Equal("Newer saved content", ConsultantReportContentSerializer.Deserialize((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson!).ExecutiveSummary.Narrative);
    }

    [Fact]
    public async Task ExecutiveSummary_Save_Requires_Antiforgery_And_Assessment_Owner()
    {
        await using var factory = new ReportPageFactory();
        var submission = BuildSubmission("3.0");
        var ids = await factory.SeedAssessmentAsync("3.0", submission);
        await factory.SeedReportAsync(ids.AssessmentId, ids.ResponseId, "3.0", submission);
        var client = factory.CreateAdminClient();
        var revision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var fields = new Dictionary<string, string>
        {
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString(),
            ["RevisionId"] = revision.Id.ToString(),
            ["ExpectedConcurrencyStamp"] = revision.ConcurrencyStamp.ToString(),
            ["Input.State"] = nameof(ReportAssessmentState.NotAssessed),
            ["Input.Narrative"] = string.Empty
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(factory.PageUrl(ids.AssessmentId, ids.ResponseId) + "?handler=SaveExecutiveSummary", new FormUrlEncodedContent(fields))).StatusCode);

        await using var otherOwnerFactory = new ReportPageFactory(ownerUserId: "other-user");
        var otherIds = await otherOwnerFactory.SeedAssessmentAsync("3.0", submission);
        await otherOwnerFactory.SeedReportAsync(otherIds.AssessmentId, otherIds.ResponseId, "3.0", submission);
        Assert.Equal(HttpStatusCode.NotFound, (await otherOwnerFactory.CreateAdminClient().GetAsync(otherOwnerFactory.PageUrl(otherIds.AssessmentId, otherIds.ResponseId))).StatusCode);

        var ownerClient = factory.CreateAdminClient();
        var ownerUrl = factory.PageUrl(ids.AssessmentId, ids.ResponseId);
        var ownerHtml = await (await ownerClient.GetAsync(ownerUrl)).Content.ReadAsStringAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var assessment = await db.Set<Assessment>().SingleAsync(item => item.Id == ids.AssessmentId);
            assessment.OwnerUserId = "other-user";
            await db.SaveChangesAsync();
        }
        var ownerRevision = (await factory.GetOnlyReportAsync()).Revisions.Single();
        var ownerPost = await ownerClient.PostAsync(ownerUrl + "?handler=SaveExecutiveSummary", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractToken(ownerHtml),
            ["AssessmentId"] = ids.AssessmentId.ToString(),
            ["SubmittedResponseId"] = ids.ResponseId.ToString(),
            ["RevisionId"] = ownerRevision.Id.ToString(),
            ["ExpectedConcurrencyStamp"] = ownerRevision.ConcurrencyStamp.ToString(),
            ["Input.State"] = nameof(ReportAssessmentState.NotAssessed),
            ["Input.Narrative"] = "Must not save after ownership changes."
        }));
        Assert.Equal(HttpStatusCode.NotFound, ownerPost.StatusCode);
        Assert.Null((await factory.GetOnlyReportAsync()).Revisions.Single().ConsultantContentJson);
    }

    private static string ExtractToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups["token"].Value);
    }

    private static QuestionnaireResponseV1 BuildSubmission(string version)
    {
        var response = new QuestionnaireResponseV1
        {
            SchemaVersion = version,
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q01"] = new() { Composite = new Dictionary<string, FieldAnswer>
                {
                    ["facility_name"] = new() { Text = "Plant" },
                    ["facility_location"] = new() { Text = "Building A" }
                } },
                ["q02"] = new() { Text = "Consultant response marker" },
                ["q04"] = new() { Selected = "11-25" },
                ["q05"] = new() { Selected = "two_shifts" },
                ["q06"] = new() { SelectedValues = new List<string> { "unplanned_downtime", "quality" } },
                ["q07"] = new() { Selected = "quality" },
                ["q11"] = new() { SelectedValues = new List<string> { "plc", "mes" } },
                ["q14"] = new() { SelectedValues = new List<string> { "auto_equipment", "db_queries" } },
                ["q15"] = new() { Composite = new Dictionary<string, FieldAnswer> { ["integration_level"] = new() { Selected = "mostly" } } },
                ["q17"] = new() { Composite = new Dictionary<string, FieldAnswer> { ["history_choice"] = new() { Selected = "most_relevant" } } },
                ["q19"] = new() { Selected = "yes" },
                ["q20"] = new() { Selected = "standardized" },
                ["q21"] = new() { Selected = "automatic" },
                ["q24"] = new() { Selected = "automated_validated" },
                ["q27"] = new() { Composite = new Dictionary<string, FieldAnswer>
                {
                    ["desired_outcome"] = new() { Text = "Reduce downtime" },
                    ["baseline_available"] = new() { Selected = "partial" }
                } },
                ["q31"] = new() { Selected = "consistently_accurate" },
                ["q32"] = new() { Selected = "proactive_decisions" },
                ["q33"] = new() { Selected = "nearly_all" },
                ["q34"] = new() { Selected = "consistently_reliable" },
                ["q35"] = new() { Selected = "systematic_monitoring" },
                ["q36"] = new() { Selected = "almost_never_missing" }
            }
        };
        var validIds = QuestionnaireDefinitionResolver.Resolve(version).Sections.SelectMany(section => section.Questions)
            .Select(question => question.Id).ToHashSet(StringComparer.Ordinal);
        response.Answers = response.Answers.Where(item => validIds.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return response;
    }

    private sealed class ReportPageFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"report-page-{Guid.NewGuid():N}";
        private readonly string _ownerUserId;

        public ReportPageFactory(string ownerUserId = "test-user") => _ownerUserId = ownerUserId;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused;Username=unused;Password=unused"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<ApplicationDbContext>();
                services.AddDbContext<ApplicationDbContext>(options => options
                    .UseInMemoryDatabase(_databaseName)
                    .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        }

        public HttpClient CreateAdminClient()
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "assessmentadmin");
            return client;
        }

        public string PageUrl(Guid assessmentId, Guid responseId)
            => $"/Internal/Assessments/Report/{assessmentId:D}/{responseId:D}";

        public async Task<(Guid AssessmentId, Guid ResponseId)> SeedAssessmentAsync(string version, QuestionnaireResponseV1 response)
        {
            var assessmentId = Guid.NewGuid();
            var responseId = Guid.NewGuid();
            await SeedAsync(db =>
            {
                var now = DateTime.UtcNow;
                db.Add(new Assessment
                {
                    Id = assessmentId,
                    Title = "Report page assessment",
                    SiteName = "Plant North",
                    OwnerUserId = _ownerUserId,
                    Status = AssessmentStatus.Submitted,
                    QuestionnaireVersion = version,
                    CreatedUtc = now,
                    UpdatedUtc = now
                });
                AddResponse(db, assessmentId, responseId, response, now);
                return Task.CompletedTask;
            });
            return (assessmentId, responseId);
        }

        public async Task<(Guid ResponseId, Guid InvitationId)> SeedSubmittedResponseAsync(Guid assessmentId, string marker, DateTime submittedUtc)
        {
            var responseId = Guid.NewGuid();
            var invitationId = Guid.NewGuid();
            await SeedAsync(db =>
            {
                if (!db.Set<Assessment>().Any(assessment => assessment.Id == assessmentId))
                {
                    db.Add(new Assessment
                    {
                        Id = assessmentId,
                        Title = "Report page assessment",
                        SiteName = "Plant North",
                        OwnerUserId = _ownerUserId,
                        Status = AssessmentStatus.Submitted,
                        QuestionnaireVersion = "3.0",
                        CreatedUtc = submittedUtc,
                        UpdatedUtc = submittedUtc
                    });
                }
                db.Add(new AssessmentInvitation
                {
                    Id = invitationId,
                    AssessmentId = assessmentId,
                    Email = "customer@example.com",
                    TokenHash = $"hash-{invitationId:N}",
                    CreatedUtc = submittedUtc,
                    ExpiresUtc = submittedUtc.AddDays(2)
                });
                var answer = BuildSubmission("3.0");
                answer.Answers["q02"].Text = marker;
                AddResponse(db, assessmentId, responseId, answer, submittedUtc, invitationId);
                return Task.CompletedTask;
            });
            return (responseId, invitationId);
        }

        public async Task SeedReportAsync(Guid assessmentId, Guid responseId, string version, QuestionnaireResponseV1 response)
        {
            await SeedAsync(db =>
            {
                var scoring = new ReadinessScoringService().Score(version, response);
                var now = DateTime.UtcNow;
                db.Add(new AssessmentReport
                {
                    Id = Guid.NewGuid(),
                    AssessmentId = assessmentId,
                    SubmittedResponseId = responseId,
                    QuestionnaireVersion = version,
                    ResponseSubmittedUtc = now,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                    CreatedByUserId = _ownerUserId,
                    UpdatedByUserId = _ownerUserId,
                    Revisions = new List<AssessmentReportRevision>
                    {
                        new()
                        {
                            Id = Guid.NewGuid(),
                            RevisionNumber = 1,
                            Status = AssessmentReportRevisionStatus.Draft,
                            ScoringSnapshotSchemaVersion = ReadinessScoringSnapshotSerializer.PersistedSchemaVersion,
                            ScoringSnapshot = ReadinessScoringSnapshotSerializer.Serialize(scoring),
                            CreatedUtc = now,
                            UpdatedUtc = now,
                            CreatedByUserId = _ownerUserId,
                            UpdatedByUserId = _ownerUserId
                        }
                    }
                });
                return Task.CompletedTask;
            });
        }

        public async Task<int> CountReportsAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Set<AssessmentReport>().CountAsync();
        }

        public async Task<AssessmentReport> GetOnlyReportAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Set<AssessmentReport>()
                .Include(report => report.Revisions).SingleAsync();
        }

        public async Task<AssessmentResponse> GetResponseAsync(Guid responseId)
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Set<AssessmentResponse>().SingleAsync(item => item.Id == responseId);
        }

        public async Task SetContentAsync(Guid revisionId, ConsultantReportContent content)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var revision = await db.Set<AssessmentReportRevision>().SingleAsync(item => item.Id == revisionId);
            revision.ConsultantContentJson = ConsultantReportContentSerializer.Serialize(content);
            revision.ConsultantContentSchemaVersion = ConsultantReportContent.CurrentSchemaVersion;
            revision.ConcurrencyStamp = Guid.NewGuid();
            revision.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        private async Task SeedAsync(Func<ApplicationDbContext, Task> seed)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await seed(db);
            await db.SaveChangesAsync();
        }

        private static void AddResponse(ApplicationDbContext db, Guid assessmentId, Guid responseId, QuestionnaireResponseV1 response, DateTime submittedUtc, Guid? invitationId = null)
        {
            invitationId ??= Guid.NewGuid();
            if (db.Set<AssessmentInvitation>().Local.All(invitation => invitation.Id != invitationId.Value))
            {
                db.Add(new AssessmentInvitation
                {
                    Id = invitationId.Value,
                    AssessmentId = assessmentId,
                    Email = "customer@example.com",
                    TokenHash = $"hash-{invitationId.Value:N}",
                    CreatedUtc = submittedUtc,
                    ExpiresUtc = submittedUtc.AddDays(2)
                });
            }
            db.Add(new AssessmentResponse
            {
                Id = responseId,
                AssessmentId = assessmentId,
                InvitationId = invitationId.Value,
                Data = QuestionnaireResponseV1Serializer.Serialize(response),
                IsDraft = false,
                LastSavedUtc = submittedUtc,
                SubmittedUtc = submittedUtc,
                ConcurrencyStamp = Guid.NewGuid().ToString()
            });
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "ReportPageTestAuth";
        public const string HeaderName = "X-Report-Test-Auth";

        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(HeaderName, out var values)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "test-user"), new(ClaimTypes.Name, "test-user") };
            if (string.Equals(values.ToString(), "assessmentadmin", StringComparison.OrdinalIgnoreCase))
                claims.Add(new Claim(ClaimTypes.Role, "AssessmentAdmin"));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
