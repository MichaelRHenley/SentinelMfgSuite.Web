using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentInvitationAccessServiceTests
{
    [Fact]
    public async Task ValidateAccessAsync_Returns_Authorized_For_Valid_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = CreateAssessment(AssessmentStatus.Invited);
        var invitation = CreateInvitation(assessment.Id, "valid-token", expiresUtc: DateTime.UtcNow.AddHours(1));
        fixture.Db.Add(assessment);
        fixture.Db.Add(invitation);
        await fixture.Db.SaveChangesAsync();

        var service = new AssessmentInvitationAccessService(fixture.Db);

        var result = await service.ValidateAccessAsync(invitation.Id, "valid-token");

        Assert.True(result.IsAuthorized);
        Assert.Equal(AssessmentInvitationAccessState.Authorized, result.AccessState);
        Assert.Equal(assessment.Id, result.AssessmentId);
        Assert.Equal(invitation.Id, result.InvitationId);
        Assert.Null(result.ResponseId);
        Assert.Equal(AssessmentInvitationResponseState.NotStarted, result.ResponseState);
        Assert.False(result.IsReadOnly);
    }

    [Fact]
    public async Task ValidateAccessAsync_Returns_Denied_For_Invalid_Token()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = CreateAssessment(AssessmentStatus.Invited);
        var invitation = CreateInvitation(assessment.Id, "valid-token", expiresUtc: DateTime.UtcNow.AddHours(1));
        fixture.Db.Add(assessment);
        fixture.Db.Add(invitation);
        await fixture.Db.SaveChangesAsync();

        var service = new AssessmentInvitationAccessService(fixture.Db);

        var result = await service.ValidateAccessAsync(invitation.Id, "wrong-token");

        Assert.Equal(AssessmentInvitationAccessState.Denied, result.AccessState);
        Assert.False(result.IsAuthorized);
        Assert.Null(result.AssessmentId);
    }

    [Fact]
    public async Task ValidateAccessAsync_Returns_Denied_For_Missing_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var service = new AssessmentInvitationAccessService(fixture.Db);

        var result = await service.ValidateAccessAsync(Guid.NewGuid(), "valid-token");

        Assert.Equal(AssessmentInvitationAccessState.Denied, result.AccessState);
        Assert.False(result.IsAuthorized);
    }

    [Fact]
    public async Task ValidateAccessAsync_Returns_Expired_For_Expired_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = CreateAssessment(AssessmentStatus.Invited);
        var invitation = CreateInvitation(assessment.Id, "valid-token", expiresUtc: DateTime.UtcNow.AddMinutes(-1));
        fixture.Db.Add(assessment);
        fixture.Db.Add(invitation);
        await fixture.Db.SaveChangesAsync();

        var service = new AssessmentInvitationAccessService(fixture.Db);

        var result = await service.ValidateAccessAsync(invitation.Id, "valid-token");

        Assert.Equal(AssessmentInvitationAccessState.Expired, result.AccessState);
        Assert.False(result.IsAuthorized);
    }

    [Fact]
    public async Task ValidateAccessAsync_Returns_Revoked_For_Revoked_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = CreateAssessment(AssessmentStatus.Invited);
        var invitation = CreateInvitation(assessment.Id, "valid-token", expiresUtc: DateTime.UtcNow.AddHours(1));
        invitation.RevokedUtc = DateTime.UtcNow.AddMinutes(-5);
        fixture.Db.Add(assessment);
        fixture.Db.Add(invitation);
        await fixture.Db.SaveChangesAsync();

        var service = new AssessmentInvitationAccessService(fixture.Db);

        var result = await service.ValidateAccessAsync(invitation.Id, "valid-token");

        Assert.Equal(AssessmentInvitationAccessState.Revoked, result.AccessState);
        Assert.False(result.IsAuthorized);
    }

    [Fact]
    public async Task ValidateAccessAsync_Returns_Ineligible_For_Cancelled_Assessment()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = CreateAssessment(AssessmentStatus.Cancelled);
        var invitation = CreateInvitation(assessment.Id, "valid-token", expiresUtc: DateTime.UtcNow.AddHours(1));
        fixture.Db.Add(assessment);
        fixture.Db.Add(invitation);
        await fixture.Db.SaveChangesAsync();

        var service = new AssessmentInvitationAccessService(fixture.Db);

        var result = await service.ValidateAccessAsync(invitation.Id, "valid-token");

        Assert.Equal(AssessmentInvitationAccessState.Ineligible, result.AccessState);
        Assert.False(result.IsAuthorized);
    }

    [Fact]
    public async Task ValidateAccessAsync_Returns_Ineligible_When_Response_Assessment_Does_Not_Match_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var invitationAssessment = CreateAssessment(AssessmentStatus.InProgress);
        var differentAssessment = CreateAssessment(AssessmentStatus.InProgress);
        var invitation = CreateInvitation(invitationAssessment.Id, "valid-token", expiresUtc: DateTime.UtcNow.AddHours(1));
        var response = new AssessmentResponse
        {
            Id = Guid.NewGuid(),
            AssessmentId = differentAssessment.Id,
            InvitationId = invitation.Id,
            Data = "{}",
            IsDraft = true,
            LastSavedUtc = DateTime.UtcNow,
            ConcurrencyStamp = Guid.NewGuid().ToString()
        };

        fixture.Db.AddRange(invitationAssessment, differentAssessment, invitation, response);
        await fixture.Db.SaveChangesAsync();

        var service = new AssessmentInvitationAccessService(fixture.Db);

        var result = await service.ValidateAccessAsync(invitation.Id, "valid-token");

        Assert.Equal(AssessmentInvitationAccessState.Ineligible, result.AccessState);
        Assert.False(result.IsAuthorized);
    }

    [Fact]
    public async Task ValidateAccessAsync_Returns_Draft_Response_State()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = CreateAssessment(AssessmentStatus.InProgress);
        var invitation = CreateInvitation(assessment.Id, "valid-token", expiresUtc: DateTime.UtcNow.AddHours(1));
        var response = new AssessmentResponse
        {
            Id = Guid.NewGuid(),
            AssessmentId = assessment.Id,
            InvitationId = invitation.Id,
            Data = "{}",
            IsDraft = true,
            LastSavedUtc = DateTime.UtcNow,
            ConcurrencyStamp = Guid.NewGuid().ToString()
        };

        fixture.Db.AddRange(assessment, invitation, response);
        await fixture.Db.SaveChangesAsync();

        var service = new AssessmentInvitationAccessService(fixture.Db);

        var result = await service.ValidateAccessAsync(invitation.Id, "valid-token");

        Assert.True(result.IsAuthorized);
        Assert.Equal(AssessmentInvitationResponseState.Draft, result.ResponseState);
        Assert.False(result.IsReadOnly);
        Assert.Equal(response.Id, result.ResponseId);
    }

    [Fact]
    public async Task ValidateAccessAsync_Returns_Submitted_Response_ReadOnly_Access()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var assessment = CreateAssessment(AssessmentStatus.Submitted);
        var invitation = CreateInvitation(assessment.Id, "valid-token", expiresUtc: DateTime.UtcNow.AddHours(1));
        var response = new AssessmentResponse
        {
            Id = Guid.NewGuid(),
            AssessmentId = assessment.Id,
            InvitationId = invitation.Id,
            Data = "{}",
            IsDraft = false,
            LastSavedUtc = DateTime.UtcNow,
            SubmittedUtc = DateTime.UtcNow.AddMinutes(-1),
            ConcurrencyStamp = Guid.NewGuid().ToString()
        };

        fixture.Db.AddRange(assessment, invitation, response);
        await fixture.Db.SaveChangesAsync();

        var service = new AssessmentInvitationAccessService(fixture.Db);

        var result = await service.ValidateAccessAsync(invitation.Id, "valid-token");

        Assert.True(result.IsAuthorized);
        Assert.Equal(AssessmentInvitationResponseState.Submitted, result.ResponseState);
        Assert.True(result.IsReadOnly);
        Assert.Equal(response.Id, result.ResponseId);
    }

    private static Assessment CreateAssessment(AssessmentStatus status)
        => new()
        {
            Id = Guid.NewGuid(),
            Status = status,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            Title = "Assessment"
        };

    private static AssessmentInvitation CreateInvitation(Guid assessmentId, string token, DateTime expiresUtc)
        => new()
        {
            Id = Guid.NewGuid(),
            AssessmentId = assessmentId,
            Email = "customer@example.com",
            TokenHash = ComputeTokenHash(token),
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = expiresUtc,
            AccessCount = 0
        };

    private static string ComputeTokenHash(string token)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
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
