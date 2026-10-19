using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentInvitationDeliveryTrackingPersistenceTests
{
    [Fact]
    public void AssessmentInvitationDeliveryStatus_Enum_Values_Are_Stable_And_Appended()
    {
        Assert.Equal(0, (short)AssessmentInvitationDeliveryStatus.Pending);
        Assert.Equal(1, (short)AssessmentInvitationDeliveryStatus.Sending);
        Assert.Equal(2, (short)AssessmentInvitationDeliveryStatus.Accepted);
        Assert.Equal(3, (short)AssessmentInvitationDeliveryStatus.Failed);
        Assert.Equal(4, (short)AssessmentInvitationDeliveryStatus.OutcomeUnknown);
    }

    [Fact]
    public void AssessmentInvitation_Defaults_To_Pending_Delivery_State()
    {
        var invitation = new AssessmentInvitation();

        Assert.Equal(AssessmentInvitationDeliveryStatus.Pending, invitation.DeliveryStatus);
        Assert.Equal(0, invitation.DeliveryAttemptCount);
        Assert.Null(invitation.DeliveryLastAttemptUtc);
        Assert.Null(invitation.DeliveryAcceptedUtc);
        Assert.Null(invitation.DeliveryFailureCode);
    }

    [Fact]
    public async Task AssessmentInvitation_Delivery_Tracking_Fields_Persist()
    {
        await using var fixture = await TestDbFixture.CreateAsync();

        var assessment = new Assessment
        {
            Id = Guid.NewGuid(),
            Title = "Assessment",
            SiteName = "Plant A",
            ContactEmail = "customer@example.com",
            Status = AssessmentStatus.Draft,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var invitation = new AssessmentInvitation
        {
            Id = Guid.NewGuid(),
            AssessmentId = assessment.Id,
            Email = "customer@example.com",
            TokenHash = "hash-value",
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddDays(7),
            DeliveryStatus = AssessmentInvitationDeliveryStatus.Sending,
            DeliveryAttemptCount = 1,
            DeliveryLastAttemptUtc = DateTime.UtcNow,
            DeliveryAcceptedUtc = null,
            DeliveryFailureCode = "TransientFailure"
        };

        fixture.Db.Add(assessment);
        fixture.Db.Add(invitation);
        await fixture.Db.SaveChangesAsync();

        invitation.DeliveryStatus = AssessmentInvitationDeliveryStatus.Accepted;
        invitation.DeliveryAttemptCount = 2;
        invitation.DeliveryAcceptedUtc = DateTime.UtcNow;
        invitation.DeliveryFailureCode = null;
        await fixture.Db.SaveChangesAsync();

        var saved = await fixture.Db.Set<AssessmentInvitation>()
            .AsNoTracking()
            .SingleAsync(x => x.Id == invitation.Id);

        Assert.Equal(AssessmentInvitationDeliveryStatus.Accepted, saved.DeliveryStatus);
        Assert.Equal(2, saved.DeliveryAttemptCount);
        Assert.NotNull(saved.DeliveryLastAttemptUtc);
        Assert.NotNull(saved.DeliveryAcceptedUtc);
        Assert.Null(saved.DeliveryFailureCode);
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
