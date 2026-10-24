using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class QuestionnaireCurrentVersionTests
{
    [Fact]
    public void CurrentVersion_Resolves_To_V3_And_Matches_Its_SchemaVersion()
    {
        var definition = QuestionnaireDefinitionResolver.Resolve(QuestionnaireDefinitionResolver.CurrentVersion);

        Assert.Equal("3.0", QuestionnaireDefinitionResolver.CurrentVersion);
        Assert.Equal(QuestionnaireV3.Definition.SchemaVersion, definition.SchemaVersion);
    }

    [Fact]
    public void CurrentDefinition_Exposes_Q33_To_Q36_And_V2_Does_Not()
    {
        static HashSet<string> Ids(QuestionnaireDefinition d) =>
            d.Sections.SelectMany(s => s.Questions).Select(q => q.Id).ToHashSet();

        var current = Ids(QuestionnaireDefinitionResolver.Resolve(QuestionnaireDefinitionResolver.CurrentVersion));
        var v2 = Ids(QuestionnaireDefinitionResolver.Resolve("2.0"));

        foreach (var id in new[] { "q33", "q34", "q35", "q36" })
        {
            Assert.Contains(id, current);
            Assert.DoesNotContain(id, v2);
        }
    }

    [Fact]
    public void Historical_Versions_Still_Resolve()
    {
        Assert.Equal("1.0", QuestionnaireDefinitionResolver.Resolve("1.0").SchemaVersion);
        Assert.Equal("2.0", QuestionnaireDefinitionResolver.Resolve("2.0").SchemaVersion);
    }

    [Theory]
    [InlineData(AssessmentProvisioningInitiationSource.InternalDraftAdmin)]
    [InlineData(AssessmentProvisioningInitiationSource.ManualAdmin)]
    public async Task Both_Provisioning_Paths_Select_The_Same_Current_Version(AssessmentProvisioningInitiationSource source)
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new AssessmentProvisioningService(fixture.Db);
        var request = new AssessmentProvisioningRequest(source, "Assessment", "Site", "customer@example.com");

        Guid? assessmentId;
        if (source == AssessmentProvisioningInitiationSource.ManualAdmin)
        {
            var manual = await service.ProvisionManualWithInvitationAtomicAsync(request, TimeSpan.FromDays(7));
            Assert.True(manual.Succeeded);
            assessmentId = manual.AssessmentId;
        }
        else
        {
            var draft = await service.ProvisionAsync(request);
            Assert.True(draft.Succeeded);
            assessmentId = draft.AssessmentId;
        }

        var saved = await fixture.Db.Set<Assessment>().FindAsync(assessmentId!.Value);
        Assert.Equal(QuestionnaireDefinitionResolver.CurrentVersion, saved!.QuestionnaireVersion);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ApplicationDbContext Db { get; }

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
