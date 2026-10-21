using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class AssessmentReportPersistenceModelTests
{
    [Fact]
    public void Report_Model_Uses_Required_Keys_Unique_Indexes_And_Restrictive_Relationships()
    {
        using var db = CreateContext();
        var report = db.Model.FindEntityType(typeof(AssessmentReport))!;
        var revision = db.Model.FindEntityType(typeof(AssessmentReportRevision))!;

        Assert.Equal(new[] { nameof(AssessmentReport.Id) }, report.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Contains(report.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(AssessmentReport.SubmittedResponseId) }));
        Assert.Contains(revision.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(AssessmentReportRevision.AssessmentReportId), nameof(AssessmentReportRevision.RevisionNumber) }));
        Assert.Equal(DeleteBehavior.Restrict, report.GetForeignKeys().Single(fk => fk.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(AssessmentReport.AssessmentId) })).DeleteBehavior);
        var responseFk = report.GetForeignKeys().Single(fk => fk.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(AssessmentReport.SubmittedResponseId) }));
        Assert.Equal(DeleteBehavior.Restrict, responseFk.DeleteBehavior);
        Assert.Equal(nameof(AssessmentResponse.Id), responseFk.PrincipalKey.Properties.Single().Name);
        Assert.All(revision.GetForeignKeys(), fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
        var supersessionFk = revision.GetForeignKeys().Single(fk => fk.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(AssessmentReportRevision.AssessmentReportId), nameof(AssessmentReportRevision.SupersededByRevisionId) }));
        Assert.Equal(new[] { nameof(AssessmentReportRevision.AssessmentReportId), nameof(AssessmentReportRevision.Id) }, supersessionFk.PrincipalKey.Properties.Select(property => property.Name));
    }

    [Fact]
    public void Report_Model_Uses_Jsonb_And_Optimistic_Concurrency_Tokens()
    {
        using var db = CreateContext();
        var report = db.Model.FindEntityType(typeof(AssessmentReport))!;
        var revision = db.Model.FindEntityType(typeof(AssessmentReportRevision))!;

        Assert.True(report.FindProperty(nameof(AssessmentReport.ConcurrencyStamp))!.IsConcurrencyToken);
        Assert.True(revision.FindProperty(nameof(AssessmentReportRevision.ConcurrencyStamp))!.IsConcurrencyToken);
        Assert.Equal("jsonb", revision.FindProperty(nameof(AssessmentReportRevision.ScoringSnapshot))!.GetColumnType());
        Assert.True(revision.FindProperty(nameof(AssessmentReportRevision.ScoringSnapshot))!.IsNullable);
        Assert.True(revision.FindProperty(nameof(AssessmentReportRevision.ScoringSnapshotSchemaVersion))!.IsNullable);
    }

    [Fact]
    public async Task Report_And_Revision_Can_Be_Persisted_Without_Response_Copy()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"assessment-report-{Guid.NewGuid():N}")
            .Options;
        var assessmentId = Guid.NewGuid();
        var responseId = Guid.NewGuid();

        await using (var db = new ApplicationDbContext(options))
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
                Data = "{\"schemaVersion\":\"3.0\",\"answers\":{}}",
                IsDraft = false,
                LastSavedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow
            });
            db.Set<AssessmentReport>().Add(new AssessmentReport
            {
                Id = Guid.NewGuid(),
                AssessmentId = assessmentId,
                SubmittedResponseId = responseId,
                QuestionnaireVersion = "3.0",
                ResponseSubmittedUtc = DateTime.UtcNow,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                CreatedByUserId = "consultant-id",
                UpdatedByUserId = "consultant-id",
                Revisions =
                {
                    new AssessmentReportRevision
                    {
                        Id = Guid.NewGuid(),
                        RevisionNumber = 1,
                        Status = AssessmentReportRevisionStatus.Draft,
                        ExecutiveSummary = "Draft summary",
                        ScopeAndMethodology = "Draft scope",
                        AssumptionsAndLimitations = "Draft limits",
                        NextSteps = "Draft steps",
                        CreatedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow,
                        CreatedByUserId = "consultant-id",
                        UpdatedByUserId = "consultant-id"
                    }
                }
            });
            await db.SaveChangesAsync();
        }

        await using var verifyDb = new ApplicationDbContext(options);
        var storedReport = await verifyDb.Set<AssessmentReport>().Include(item => item.Revisions).SingleAsync();
        var storedResponse = await verifyDb.Set<AssessmentResponse>().SingleAsync();
        Assert.Equal(responseId, storedReport.SubmittedResponseId);
        Assert.Single(storedReport.Revisions);
        Assert.Null(storedReport.Revisions.Single().ScoringSnapshot);
        Assert.Equal("{\"schemaVersion\":\"3.0\",\"answers\":{}}", storedResponse.Data);
    }

    private static ApplicationDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
}
