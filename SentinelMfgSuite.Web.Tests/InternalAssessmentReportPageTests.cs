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
