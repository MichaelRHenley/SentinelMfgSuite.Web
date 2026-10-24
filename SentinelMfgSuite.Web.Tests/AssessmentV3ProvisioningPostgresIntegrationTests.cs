using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

/// <summary>
/// Real-service PostgreSQL validation that a newly provisioned assessment uses the current (V3) questionnaire,
/// persists q33-q36, is scored with an overall result, and that a historical V2 assessment keeps its V2 behavior.
/// Uses a fictional manufacturer only; scores are verified against the contract, not hard-coded.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
public sealed class AssessmentV3ProvisioningPostgresIntegrationTests
{
    private const string Contact = "ops.lead@lakeshore-precision.example";

    private static QuestionAnswer S(string v) => new() { Selected = v };
    private static QuestionAnswer M(params string[] v) => new() { SelectedValues = v.ToList() };
    private static QuestionAnswer T(string v) => new() { Text = v };
    private static FieldAnswer FT(string v) => new() { Text = v };
    private static FieldAnswer FS(string v) => new() { Selected = v };

    private static QuestionnaireResponseV1 BuildResponse(string version)
    {
        var r = new QuestionnaireResponseV1 { SchemaVersion = version };
        var a = r.Answers;
        a["q01"] = new() { Composite = new() { ["facility_name"] = FT("Lakeshore Precision Components - Plant 2"), ["facility_location"] = FT("Fictionville (fictional)") } };
        a["q02"] = T("Machined aluminium housings, stamped steel brackets and actuator modules.");
        a["q03"] = M("discrete", "batch", "material_handling");
        a["q04"] = S("11-25");
        a["q05"] = S("three_shifts");
        a["q06"] = M("unplanned_downtime", "manual_data", "visibility");
        a["q07"] = S("unplanned_downtime");
        a["q08"] = T("Operators log stoppages on paper; supervisors re-key totals into Excel.");
        a["q09"] = new() { Composite = new() { ["impact_choice"] = FS("estimated"), ["impact_details"] = FT("Estimated, not validated.") } };
        a["q10"] = T("Reliable stop reasons by machine.");
        a["q11"] = M("plc", "scada", "mes", "erp", "cmms", "quality", "manual");
        a["q12"] = M("plc", "scada", "mes", "cmms", "manual");
        a["q13"] = new() { Repeatable = new()
        {
            new() { ["source_name"] = FT("CNC cell controllers"), ["system_type"] = FS("plc"), ["available_data"] = FT("Machine state"), ["connection_method"] = FS("opc_ua"), ["access_status"] = FS("requires_internal") },
            new() { ["source_name"] = FT("Paper downtime logs"), ["system_type"] = FS("manual"), ["available_data"] = FT("Stop reasons"), ["connection_method"] = FS("no_connection"), ["access_status"] = FS("unavailable") }
        } };
        a["q14"] = M("auto_equipment", "db_queries", "manual_entry", "paper");
        a["q15"] = new() { Composite = new() { ["integration_level"] = FS("manual_transfers"), ["integration_notes"] = FT("Nightly file exchange between MES and ERP.") } };
        a["q16"] = T("No single data owner.");
        a["q17"] = new() { Composite = new() { ["history_choice"] = FS("some_sources"), ["retention"] = FS("varies") } };
        a["q18"] = new() { Composite = new() { ["frequency_choice"] = FS("varies"), ["frequency_notes"] = FT("Seconds where collected; manual per shift.") } };
        a["q19"] = S("partial");
        a["q20"] = S("inconsistent");
        a["q21"] = S("manual_match");
        a["q22"] = M("missing", "inconsistent_naming", "timestamp_issues", "manual_errors", "downtime_codes");
        a["q23"] = M("excel", "mes_erp_reporting", "power_bi");
        a["q24"] = S("mix");
        a["q25"] = S("evaluated");
        a["q26"] = M("controls", "it_networking", "bi_reporting", "external_vendors");
        a["q27"] = new() { Composite = new() { ["desired_outcome"] = FT("Reduce unplanned downtime."), ["baseline_available"] = FS("partial") } };
        a["q28"] = S("3_6m");
        a["q29"] = M("budget", "ot_cyber", "staffing", "vendor_deps");
        a["q30"] = M("plant_ops", "maintenance", "controls", "it_ot");
        a["q31"] = S("frequent_discrepancies");
        a["q32"] = S("retrospective_reporting");
        if (version == "3.0")
        {
            a["q33"] = S("about_quarter_to_half");
            a["q34"] = S("sometimes_late_or_incomplete");
            a["q35"] = S("informal_checks");
            a["q36"] = S("often_missing");
        }
        return r;
    }

    private static async Task<(Guid AssessmentId, Guid ResponseId)> ProvisionAndSubmitAsync(
        PostgresIntegrationTestFixture fixture, string userId, string? forceVersion)
    {
        Guid assessmentId, invitationId;
        string token;
        await using (var db = fixture.CreateDbContext())
        {
            var prov = await new AssessmentProvisioningService(db).ProvisionAsync(new AssessmentProvisioningRequest(
                AssessmentProvisioningInitiationSource.InternalDraftAdmin, "Lakeshore Precision - AI Readiness", "Plant 2", Contact, ownerUserId: userId));
            Assert.True(prov.Succeeded);
            assessmentId = prov.AssessmentId!.Value;
            var inv = await new AssessmentInvitationIssuanceService(db).IssueAsync(assessmentId, TimeSpan.FromDays(7));
            Assert.True(inv.Succeeded);
            invitationId = inv.InvitationId!.Value;
            token = inv.InvitationToken!;

            if (forceVersion is not null)
            {
                // Arrange a pre-existing historical assessment; provisioning itself no longer produces V2.
                await db.Set<Assessment>().Where(x => x.Id == assessmentId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.QuestionnaireVersion, forceVersion));
            }
        }

        string version;
        await using (var db = fixture.CreateDbContext())
        {
            version = (await db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == assessmentId)).QuestionnaireVersion;
        }

        var payload = QuestionnaireResponseV1Serializer.Serialize(BuildResponse(version));
        string stamp;
        await using (var db = fixture.CreateDbContext())
        {
            var svc = new AssessmentQuestionnaireService(db, new AssessmentInvitationAccessService(db));
            var save = await svc.SaveDraftAsync(invitationId, token, payload, null);
            Assert.Equal(AssessmentQuestionnaireSaveState.Success, save.State);
            stamp = save.ConcurrencyStamp!;
        }
        await using (var db = fixture.CreateDbContext())
        {
            var svc = new AssessmentQuestionnaireService(db, new AssessmentInvitationAccessService(db));
            var sub = await svc.SubmitAsync(invitationId, token, payload, stamp);
            Assert.True(sub.IsSuccess);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var response = await db.Set<AssessmentResponse>().AsNoTracking().SingleAsync(x => x.AssessmentId == assessmentId && !x.IsDraft);
            return (assessmentId, response.Id);
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task NewAssessment_UsesV3_PersistsQ33ToQ36_AndScoresAllDimensionsAndOverall()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var userId = $"v3-prov-{Guid.NewGuid():N}";
        var caller = AssessmentReportPostgresIntegrationTests.Admin(userId);

        var (assessmentId, responseId) = await ProvisionAndSubmitAsync(fixture, userId, forceVersion: null);

        await using (var db = fixture.CreateDbContext())
        {
            var saved = await db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == assessmentId);
            Assert.Equal(QuestionnaireDefinitionResolver.CurrentVersion, saved.QuestionnaireVersion);
            Assert.Equal("3.0", saved.QuestionnaireVersion);

            var response = await db.Set<AssessmentResponse>().AsNoTracking().SingleAsync(x => x.Id == responseId);
            var persisted = QuestionnaireResponseV1Serializer.Deserialize(response.Data)!;
            Assert.Equal("3.0", persisted.SchemaVersion);
            foreach (var id in new[] { "q33", "q34", "q35", "q36" })
            {
                Assert.True(persisted.Answers.ContainsKey(id), $"{id} was not persisted.");
            }
        }

        ReadinessScoringSnapshot snapshot;
        await using (var db = fixture.CreateDbContext())
        {
            var created = await new AssessmentReportService(db, new ReadinessScoringService()).CreateAsync(assessmentId, responseId, caller);
            Assert.Equal(AssessmentReportServiceState.Created, created.State);
            var revision = created.Report!.Revisions.Single();
            Assert.Equal(ReadinessScoringSnapshotSerializer.PersistedSchemaVersion, revision.ScoringSnapshotSchemaVersion);
            var envelope = ReadinessScoringSnapshotSerializer.Deserialize(revision.ScoringSnapshot!);
            Assert.Equal(ReadinessScoringSnapshotSerializer.CurrentSchemaVersion, envelope.SchemaVersion);
            snapshot = envelope.Snapshot;
        }

        Assert.Equal("3.0", snapshot.QuestionnaireVersion);
        Assert.Equal(ReadinessOverallStatus.Eligible, snapshot.OverallStatus);
        Assert.NotNull(snapshot.OverallRawScore0To100);
        Assert.Equal(new[] { "D1", "D2", "D3" }, snapshot.Dimensions.Select(d => d.DimensionId).OrderBy(x => x).ToArray());
        Assert.All(snapshot.Dimensions, d => Assert.Equal(ReadinessDimensionStatus.Eligible, d.Status));
        var weighted = snapshot.Dimensions.Sum(d => d.RawScore0To100!.Value * (double)d.Weight);
        Assert.Equal(weighted, snapshot.OverallRawScore0To100!.Value, 6);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task HistoricalV2Assessment_RemainsV2_WithheldOverall_AndIsNotModifiedByNewProvisioning()
    {
        var fixture = AssessmentReportPostgresIntegrationTests.CreateFixtureOrSkip();
        await fixture.InitializeAsync();
        var userId = $"v2-hist-{Guid.NewGuid():N}";
        var caller = AssessmentReportPostgresIntegrationTests.Admin(userId);

        var (assessmentId, responseId) = await ProvisionAndSubmitAsync(fixture, userId, forceVersion: "2.0");

        async Task<string> Fingerprint()
        {
            await using var db = fixture.CreateDbContext();
            var a = await db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == assessmentId);
            var r = await db.Set<AssessmentResponse>().AsNoTracking().SingleAsync(x => x.Id == responseId);
            return $"{a.QuestionnaireVersion}|{a.Status}|{a.UpdatedUtc:O}|{r.Data}|{r.SubmittedUtc:O}";
        }

        var before = await Fingerprint();
        Assert.StartsWith("2.0|", before);

        await using (var db = fixture.CreateDbContext())
        {
            var prov = await new AssessmentProvisioningService(db).ProvisionAsync(new AssessmentProvisioningRequest(
                AssessmentProvisioningInitiationSource.InternalDraftAdmin, "Another", "Plant 3", Contact, ownerUserId: userId));
            Assert.True(prov.Succeeded);
        }

        Assert.Equal(before, await Fingerprint());

        await using (var db = fixture.CreateDbContext())
        {
            var created = await new AssessmentReportService(db, new ReadinessScoringService()).CreateAsync(assessmentId, responseId, caller);
            Assert.Equal(AssessmentReportServiceState.Created, created.State);
            var snapshot = ReadinessScoringSnapshotSerializer.Deserialize(created.Report!.Revisions.Single().ScoringSnapshot!).Snapshot;
            Assert.Equal("2.0", snapshot.QuestionnaireVersion);
            Assert.Equal(ReadinessOverallStatus.Withheld, snapshot.OverallStatus);
            Assert.Null(snapshot.OverallRawScore0To100);
        }
    }
}
