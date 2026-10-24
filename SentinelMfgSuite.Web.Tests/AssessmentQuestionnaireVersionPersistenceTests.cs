using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentQuestionnaireVersionPersistenceTests
{
    [Fact]
    public void Assessment_Default_QuestionnaireVersion_Is_1_0()
    {
        var assessment = new Assessment();

        Assert.Equal("1.0", assessment.QuestionnaireVersion);
    }

    [Fact]
    public async Task ProvisionAsync_For_InternalDraftAdmin_Initializes_QuestionnaireVersion_To_Current_3_0()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var service = new AssessmentProvisioningService(fixture.Db);

        var request = new AssessmentProvisioningRequest(
            AssessmentProvisioningInitiationSource.InternalDraftAdmin,
            "Assessment",
            "Site",
            "customer@example.com");

        var result = await service.ProvisionAsync(request);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.AssessmentId);

        var saved = await fixture.Db.Set<Assessment>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == result.AssessmentId!.Value);

        Assert.Equal("3.0", saved.QuestionnaireVersion);
    }

    [Fact]
    public async Task ProvisionManualWithInvitationAtomicAsync_Initializes_QuestionnaireVersion_To_Current_3_0()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var service = new AssessmentProvisioningService(fixture.Db);

        var request = new AssessmentProvisioningRequest(
            AssessmentProvisioningInitiationSource.ManualAdmin,
            "Assessment",
            "Site",
            "customer@example.com");

        var result = await service.ProvisionManualWithInvitationAtomicAsync(request, TimeSpan.FromDays(7));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.AssessmentId);

        var saved = await fixture.Db.Set<Assessment>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == result.AssessmentId!.Value);

        Assert.Equal("3.0", saved.QuestionnaireVersion);
    }

    [Fact]
    public async Task QuestionnaireVersion_Is_Persisted_And_Retrieved_Correctly()
    {
        await using var fixture = await TestDbFixture.CreateAsync();

        var assessment = new Assessment
        {
            Id = Guid.NewGuid(),
            Title = "Assessment",
            SiteName = "Site",
            ContactEmail = "customer@example.com",
            Status = AssessmentStatus.Draft,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            QuestionnaireVersion = "1.0"
        };

        fixture.Db.Set<Assessment>().Add(assessment);
        await fixture.Db.SaveChangesAsync();

        var reloaded = await fixture.Db.Set<Assessment>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == assessment.Id);

        Assert.Equal("1.0", reloaded.QuestionnaireVersion);
    }

    [Fact]
    public async Task Existing_Assessment_Behavior_Remains_Unchanged_When_QuestionnaireVersion_Not_Explicitly_Set()
    {
        await using var fixture = await TestDbFixture.CreateAsync();

        var assessment = new Assessment
        {
            Id = Guid.NewGuid(),
            Title = "Legacy Assessment",
            SiteName = "Legacy Site",
            ContactEmail = "legacy@example.com",
            Status = AssessmentStatus.Invited,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        fixture.Db.Set<Assessment>().Add(assessment);
        await fixture.Db.SaveChangesAsync();

        var reloaded = await fixture.Db.Set<Assessment>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == assessment.Id);

        Assert.Equal(AssessmentStatus.Invited, reloaded.Status);
        Assert.Equal("Legacy Assessment", reloaded.Title);
        Assert.Equal("1.0", reloaded.QuestionnaireVersion);
    }

    private sealed class TestDbFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public ApplicationDbContext Db { get; }

        private TestDbFixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public static async Task<TestDbFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;

            var db = new ApplicationDbContext(options);
            await db.Database.EnsureCreatedAsync();

            return new TestDbFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
