using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class AssessmentQuestionnaireServiceTests
{
    [Fact]
    public async Task LoadDraftAsync_Returns_Empty_Response_For_New_Invitation()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.NotStarted);
        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var result = await service.LoadDraftAsync(access.InvitationId, "token");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Response);
        Assert.Equal("1.0", result.Response!.SchemaVersion);
        Assert.Empty(result.Response.Answers);
        Assert.Null(result.ConcurrencyStamp);
        Assert.False(result.IsReadOnly);
        Assert.Equal(0, await fixture.Db.Set<AssessmentResponse>().CountAsync());
    }

    [Fact]
    public async Task LoadDraftAsync_Returns_Existing_Draft_And_ConcurrencyStamp()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.Draft);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        var response = new AssessmentResponse
        {
            Id = access.ResponseId!.Value,
            AssessmentId = access.AssessmentId,
            InvitationId = access.InvitationId,
            Data = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1
            {
                Answers = new Dictionary<string, QuestionAnswer>
                {
                    ["q02"] = new QuestionAnswer { Text = "Widgets" }
                }
            }),
            IsDraft = true,
            LastSavedUtc = DateTime.UtcNow,
            ConcurrencyStamp = "stamp-1"
        };
        fixture.Db.Add(response);
        await fixture.Db.SaveChangesAsync();

        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var result = await service.LoadDraftAsync(access.InvitationId, "token");

        Assert.True(result.IsSuccess);
        Assert.Equal("Widgets", result.Response!.Answers["q02"].Text);
        Assert.Equal("stamp-1", result.ConcurrencyStamp);
    }

    [Fact]
    public async Task SaveDraftAsync_Creates_First_Draft()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.NotStarted);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var payload = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1
        {
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q02"] = new QuestionAnswer { Text = "Widgets" }
            }
        });

        var result = await service.SaveDraftAsync(access.InvitationId, "token", payload, null);

        Assert.True(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.ConcurrencyStamp));

        var persisted = await fixture.Db.Set<AssessmentResponse>().SingleAsync();
        Assert.Equal(access.AssessmentId, persisted.AssessmentId);
        Assert.Equal(access.InvitationId, persisted.InvitationId);
        Assert.True(persisted.IsDraft);
        Assert.Null(persisted.SubmittedUtc);
        Assert.Equal(result.ConcurrencyStamp, persisted.ConcurrencyStamp);
    }

    [Fact]
    public async Task SaveDraftAsync_Updates_Existing_Draft_And_Rotates_ConcurrencyStamp()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.Draft);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        fixture.Db.Add(new AssessmentResponse
        {
            Id = access.ResponseId!.Value,
            AssessmentId = access.AssessmentId,
            InvitationId = access.InvitationId,
            Data = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1
            {
                Answers = new Dictionary<string, QuestionAnswer>
                {
                    ["q02"] = new QuestionAnswer { Text = "Old" }
                }
            }),
            IsDraft = true,
            LastSavedUtc = DateTime.UtcNow.AddMinutes(-5),
            ConcurrencyStamp = "stamp-1"
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);
        var payload = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1
        {
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q02"] = new QuestionAnswer { Text = "New" }
            }
        });

        var result = await service.SaveDraftAsync(access.InvitationId, "token", payload, "stamp-1");

        Assert.True(result.IsSuccess);
        Assert.NotEqual("stamp-1", result.ConcurrencyStamp);

        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.Set<AssessmentResponse>().AsNoTracking().SingleAsync();
        var deserialized = QuestionnaireResponseV1Serializer.Deserialize(persisted.Data);
        Assert.Equal("New", deserialized!.Answers["q02"].Text);
        Assert.Equal(result.ConcurrencyStamp, persisted.ConcurrencyStamp);
    }

    [Fact]
    public async Task SaveDraftAsync_Rejects_Invalid_Draft()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.NotStarted);
        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);
        var payload = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1
        {
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q04"] = new QuestionAnswer { Selected = "bogus" }
            }
        });

        var result = await service.SaveDraftAsync(access.InvitationId, "token", payload, null);

        Assert.Equal(AssessmentQuestionnaireSaveState.ValidationFailure, result.State);
        Assert.Contains(result.ValidationErrors, error => error.QuestionId == "q04");
        Assert.Equal(0, await fixture.Db.Set<AssessmentResponse>().CountAsync());
    }

    [Fact]
    public async Task SaveDraftAsync_Rejects_Malformed_Json()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.NotStarted);
        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var result = await service.SaveDraftAsync(access.InvitationId, "token", "{not-json", null);

        Assert.Equal(AssessmentQuestionnaireSaveState.ValidationFailure, result.State);
        Assert.Contains(result.ValidationErrors, error => error.Message.Contains("Malformed questionnaire JSON", StringComparison.Ordinal));
        Assert.Equal(0, await fixture.Db.Set<AssessmentResponse>().CountAsync());
    }

    [Fact]
    public async Task SaveDraftAsync_Returns_Conflict_For_Stale_ConcurrencyStamp()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.Draft);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        fixture.Db.Add(new AssessmentResponse
        {
            Id = access.ResponseId!.Value,
            AssessmentId = access.AssessmentId,
            InvitationId = access.InvitationId,
            Data = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1()),
            IsDraft = true,
            LastSavedUtc = DateTime.UtcNow,
            ConcurrencyStamp = "current-stamp"
        });
        await fixture.Db.SaveChangesAsync();

        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);
        var payload = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1());

        var result = await service.SaveDraftAsync(access.InvitationId, "token", payload, "stale-stamp");

        Assert.Equal(AssessmentQuestionnaireSaveState.ConcurrencyConflict, result.State);
    }

    [Fact]
    public async Task SaveDraftAsync_Returns_Conflict_For_Concurrent_First_Save()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.NotStarted);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        fixture.Db.Add(new AssessmentResponse
        {
            Id = Guid.NewGuid(),
            AssessmentId = access.AssessmentId,
            InvitationId = access.InvitationId,
            Data = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1()),
            IsDraft = true,
            LastSavedUtc = DateTime.UtcNow,
            ConcurrencyStamp = "existing-stamp"
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);
        var payload = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1());

        var result = await service.SaveDraftAsync(access.InvitationId, "token", payload, null);

        Assert.Equal(AssessmentQuestionnaireSaveState.ConcurrencyConflict, result.State);
    }

    [Fact]
    public async Task SaveDraftAsync_Returns_AlreadySubmitted_After_Submission()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.Submitted, isReadOnly: true);
        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);
        var payload = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1());

        var result = await service.SaveDraftAsync(access.InvitationId, "token", payload, "any-stamp");

        Assert.Equal(AssessmentQuestionnaireSaveState.AlreadySubmitted, result.State);
    }

    [Fact]
    public async Task SaveDraftAsync_Returns_AlreadySubmitted_For_Concurrent_Submission_Protection()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.Draft, isReadOnly: false);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        fixture.Db.Add(new AssessmentResponse
        {
            Id = access.ResponseId!.Value,
            AssessmentId = access.AssessmentId,
            InvitationId = access.InvitationId,
            Data = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1()),
            IsDraft = false,
            SubmittedUtc = DateTime.UtcNow.AddSeconds(-1),
            LastSavedUtc = DateTime.UtcNow.AddMinutes(-1),
            ConcurrencyStamp = "submitted-stamp"
        });
        await fixture.Db.SaveChangesAsync();

        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);
        var payload = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1());

        var result = await service.SaveDraftAsync(access.InvitationId, "token", payload, "draft-stamp");

        Assert.Equal(AssessmentQuestionnaireSaveState.AlreadySubmitted, result.State);
    }

    [Fact]
    public async Task LoadAndSaveDraftAsync_Return_AccessDenied_For_Invalid_Invitation_Access()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var accessService = new StubAssessmentInvitationAccessService(AssessmentInvitationAccessResult.Denied());
        var service = new AssessmentQuestionnaireService(fixture.Db, accessService);
        var payload = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1());

        var loadResult = await service.LoadDraftAsync(Guid.NewGuid(), "token");
        var saveResult = await service.SaveDraftAsync(Guid.NewGuid(), "token", payload, null);

        Assert.Equal(AssessmentQuestionnaireLoadState.AccessDenied, loadResult.State);
        Assert.Equal(AssessmentQuestionnaireSaveState.AccessDenied, saveResult.State);
    }

    [Fact]
    public async Task SubmitAsync_Finalizes_Existing_Draft_And_Assessment()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.Draft);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        fixture.Db.Add(new AssessmentResponse
        {
            Id = access.ResponseId!.Value,
            AssessmentId = access.AssessmentId,
            InvitationId = access.InvitationId,
            Data = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1()),
            IsDraft = true,
            LastSavedUtc = DateTime.UtcNow.AddMinutes(-10),
            ConcurrencyStamp = "draft-stamp"
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var result = await service.SubmitAsync(access.InvitationId, "token", CreateValidSubmissionPayload(), "draft-stamp");

        Assert.True(result.IsSuccess);
        Assert.NotEqual("draft-stamp", result.ConcurrencyStamp);

        fixture.Db.ChangeTracker.Clear();
        var response = await fixture.Db.Set<AssessmentResponse>().AsNoTracking().SingleAsync();
        var assessment = await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync();
        Assert.False(response.IsDraft);
        Assert.NotNull(response.SubmittedUtc);
        Assert.Equal(result.ConcurrencyStamp, response.ConcurrencyStamp);
        Assert.Equal(AssessmentStatus.Submitted, assessment.Status);
    }

    [Fact]
    public async Task SubmitAsync_Creates_Final_Response_Without_Saved_Draft()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.NotStarted);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var result = await service.SubmitAsync(access.InvitationId, "token", CreateValidSubmissionPayload(), null);

        Assert.True(result.IsSuccess);

        fixture.Db.ChangeTracker.Clear();
        var response = await fixture.Db.Set<AssessmentResponse>().AsNoTracking().SingleAsync();
        var assessment = await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync();
        Assert.False(response.IsDraft);
        Assert.NotNull(response.SubmittedUtc);
        Assert.Equal(AssessmentStatus.Submitted, assessment.Status);
    }

    [Fact]
    public async Task SubmitAsync_Rejects_Missing_Required_Answers()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.NotStarted);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var payload = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1
        {
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q02"] = new QuestionAnswer { Text = "Widgets" }
            }
        });

        var result = await service.SubmitAsync(access.InvitationId, "token", payload, null);

        Assert.Equal(AssessmentQuestionnaireSubmitState.ValidationFailure, result.State);
        Assert.Contains(result.ValidationErrors, error => error.QuestionId == "q01");
        Assert.Empty(await fixture.Db.Set<AssessmentResponse>().ToListAsync());
    }

    [Fact]
    public async Task SubmitAsync_Rejects_Invalid_Q06_Q07_Relationship()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.NotStarted);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var response = CreateValidSubmissionResponse();
        response.Answers["q07"] = new QuestionAnswer { Selected = "manual_data" };

        var result = await service.SubmitAsync(access.InvitationId, "token", QuestionnaireResponseV1Serializer.Serialize(response), null);

        Assert.Equal(AssessmentQuestionnaireSubmitState.ValidationFailure, result.State);
        Assert.Contains(result.ValidationErrors, error => error.QuestionId == "q07");
    }

    [Fact]
    public async Task SubmitAsync_Rejects_Malformed_Json()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.NotStarted);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var result = await service.SubmitAsync(access.InvitationId, "token", "{bad-json", null);

        Assert.Equal(AssessmentQuestionnaireSubmitState.ValidationFailure, result.State);
        Assert.Contains(result.ValidationErrors, error => error.Message.Contains("Malformed questionnaire JSON", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubmitAsync_Returns_Conflict_For_Stale_ConcurrencyStamp()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.Draft);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        fixture.Db.Add(new AssessmentResponse
        {
            Id = access.ResponseId!.Value,
            AssessmentId = access.AssessmentId,
            InvitationId = access.InvitationId,
            Data = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1()),
            IsDraft = true,
            LastSavedUtc = DateTime.UtcNow,
            ConcurrencyStamp = "current-stamp"
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var result = await service.SubmitAsync(access.InvitationId, "token", CreateValidSubmissionPayload(), "stale-stamp");

        Assert.Equal(AssessmentQuestionnaireSubmitState.ConcurrencyConflict, result.State);
    }

    [Fact]
    public async Task SubmitAsync_Returns_AlreadySubmitted_For_Existing_Submitted_Response()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.Submitted, isReadOnly: true);
        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var result = await service.SubmitAsync(access.InvitationId, "token", CreateValidSubmissionPayload(), "submitted-stamp");

        Assert.Equal(AssessmentQuestionnaireSubmitState.AlreadySubmitted, result.State);
    }

    [Fact]
    public async Task SubmitAsync_Protects_Against_Concurrent_Draft_Save_Versus_Submission()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.Draft);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        fixture.Db.Add(new AssessmentResponse
        {
            Id = access.ResponseId!.Value,
            AssessmentId = access.AssessmentId,
            InvitationId = access.InvitationId,
            Data = QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1()),
            IsDraft = false,
            SubmittedUtc = DateTime.UtcNow.AddSeconds(-1),
            LastSavedUtc = DateTime.UtcNow.AddMinutes(-1),
            ConcurrencyStamp = "submitted-stamp"
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var result = await service.SubmitAsync(access.InvitationId, "token", CreateValidSubmissionPayload(), "draft-stamp");

        Assert.Equal(AssessmentQuestionnaireSubmitState.AlreadySubmitted, result.State);
    }

    [Fact]
    public async Task SubmitAsync_Protects_Against_Concurrent_Duplicate_Submission()
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.NotStarted);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        fixture.Db.Add(new AssessmentResponse
        {
            Id = Guid.NewGuid(),
            AssessmentId = access.AssessmentId,
            InvitationId = access.InvitationId,
            Data = CreateValidSubmissionPayload(),
            IsDraft = false,
            SubmittedUtc = DateTime.UtcNow.AddSeconds(-1),
            LastSavedUtc = DateTime.UtcNow.AddMinutes(-1),
            ConcurrencyStamp = "submitted-stamp"
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var result = await service.SubmitAsync(access.InvitationId, "token", CreateValidSubmissionPayload(), null);

        Assert.Equal(AssessmentQuestionnaireSubmitState.AlreadySubmitted, result.State);
    }

    [Theory]
    [InlineData(AssessmentStatus.Cancelled)]
    [InlineData(AssessmentStatus.Completed)]
    public async Task SubmitAsync_Returns_AccessDenied_When_Assessment_Is_No_Longer_Eligible(AssessmentStatus status)
    {
        await using var fixture = await TestDbFixture.CreateAsync();
        var access = AuthorizedAccess(AssessmentInvitationResponseState.NotStarted);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access, status);
        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        var result = await service.SubmitAsync(access.InvitationId, "token", CreateValidSubmissionPayload(), null);

        Assert.Equal(AssessmentQuestionnaireSubmitState.AccessDenied, result.State);
        Assert.Empty(await fixture.Db.Set<AssessmentResponse>().ToListAsync());
        var assessment = await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == access.AssessmentId);
        Assert.Equal(status, assessment.Status);
    }

    [Fact]
    public async Task SubmitAsync_Rolls_Back_Response_When_Assessment_Status_Update_Fails()
    {
        await using var fixture = await TestDbFixture.CreateAsync(new FailAssessmentUpdateCommandInterceptor());
        var access = AuthorizedAccess(AssessmentInvitationResponseState.NotStarted);
        await SeedAuthorizedEntitiesAsync(fixture.Db, access);
        var service = new AssessmentQuestionnaireService(fixture.Db, access.Service);

        await Assert.ThrowsAnyAsync<Exception>(() => service.SubmitAsync(access.InvitationId, "token", CreateValidSubmissionPayload(), null));

        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.Set<AssessmentResponse>().AsNoTracking().ToListAsync());
        var assessment = await fixture.Db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == access.AssessmentId);
        Assert.Equal(AssessmentStatus.InProgress, assessment.Status);
    }

    private static AuthorizedAccessContext AuthorizedAccess(AssessmentInvitationResponseState responseState, bool isReadOnly = false)
    {
        var assessmentId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var responseId = responseState == AssessmentInvitationResponseState.NotStarted ? (Guid?)null : Guid.NewGuid();
        var result = AssessmentInvitationAccessResult.Authorized(assessmentId, invitationId, responseId, responseState, isReadOnly);
        return new AuthorizedAccessContext(assessmentId, invitationId, responseId, new StubAssessmentInvitationAccessService(result));
    }

    private static async Task SeedAuthorizedEntitiesAsync(ApplicationDbContext db, AuthorizedAccessContext access, AssessmentStatus status = AssessmentStatus.InProgress)
    {
        db.Add(new Assessment
        {
            Id = access.AssessmentId,
            Status = status,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            Title = "Assessment"
        });

        db.Add(new AssessmentInvitation
        {
            Id = access.InvitationId,
            AssessmentId = access.AssessmentId,
            Email = "customer@example.com",
            TokenHash = "seeded-hash",
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddHours(1)
        });

        await db.SaveChangesAsync();
    }

    private static QuestionnaireResponseV1 CreateValidSubmissionResponse()
    {
        return new QuestionnaireResponseV1
        {
            Answers = new Dictionary<string, QuestionAnswer>
            {
                ["q01"] = new QuestionAnswer
                {
                    Composite = new Dictionary<string, FieldAnswer>
                    {
                        ["facility_name"] = new FieldAnswer { Text = "North Plant" },
                        ["facility_location"] = new FieldAnswer { Text = "Building A" }
                    }
                },
                ["q04"] = new QuestionAnswer { Selected = "11-25" },
                ["q05"] = new QuestionAnswer { Selected = "two_shifts" },
                ["q06"] = new QuestionAnswer { SelectedValues = new List<string> { "unplanned_downtime", "quality" } },
                ["q07"] = new QuestionAnswer { Selected = "quality" },
                ["q11"] = new QuestionAnswer { SelectedValues = new List<string> { "plc", "mes" } },
                ["q14"] = new QuestionAnswer { SelectedValues = new List<string> { "auto_equipment", "db_queries" } },
                ["q27"] = new QuestionAnswer
                {
                    Composite = new Dictionary<string, FieldAnswer>
                    {
                        ["desired_outcome"] = new FieldAnswer { Text = "Reduce downtime by 20%" },
                        ["baseline_available"] = new FieldAnswer { Selected = "partial" }
                    }
                }
            }
        };
    }

    private static string CreateValidSubmissionPayload()
        => QuestionnaireResponseV1Serializer.Serialize(CreateValidSubmissionResponse());

    private sealed record AuthorizedAccessContext(Guid AssessmentId, Guid InvitationId, Guid? ResponseId, StubAssessmentInvitationAccessService Service);

    private sealed class StubAssessmentInvitationAccessService : IAssessmentInvitationAccessService
    {
        private readonly AssessmentInvitationAccessResult _result;

        public StubAssessmentInvitationAccessService(AssessmentInvitationAccessResult result)
        {
            _result = result;
        }

        public Task<AssessmentInvitationAccessResult> ValidateAccessAsync(Guid invitationId, string? invitationToken, CancellationToken ct = default)
            => Task.FromResult(_result);
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

        public static Task<TestDbFixture> CreateAsync()
            => CreateAsync(null);

        public static async Task<TestDbFixture> CreateAsync(DbCommandInterceptor? interceptor)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection);

            if (interceptor is not null)
            {
                optionsBuilder.AddInterceptors(interceptor);
            }

            var db = new ApplicationDbContext(optionsBuilder.Options);
            await db.Database.EnsureCreatedAsync();

            return new TestDbFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FailAssessmentUpdateCommandInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            if (command.CommandText.Contains("UPDATE \"Assessment\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected assessment update failure.");
            }

            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE \"Assessment\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected assessment update failure.");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
