using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Models.Email;
using SentinelMfgSuite.Web.Services;
using SentinelMfgSuite.Web.Services.Assessments;
using SentinelMfgSuite.Web.Services.Email;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "PostgresIntegration")]
public class EmailWorkflowPostgresTests
{
    private const string Consultant = "consultant@sentinelaisystems.net";

    private static EmailSettings Settings(string? consultant = Consultant) => new()
    {
        FromAddress = "noreply@sentinelaisystems.net",
        ConsultantNotificationAddress = consultant ?? string.Empty
    };

    [Fact]
    public async Task Successful_Submission_Queues_Exactly_One_Customer_And_One_Consultant_Message()
    {
        var pg = new PostgresIntegrationTestFixture();
        await pg.InitializeAsync();
        var ids = new Ids();
        try
        {
            await using var db = pg.CreateDbContext();
            var customer = $"c{Guid.NewGuid():N}@example.com";
            await Seed(db, ids, customer);

            var result = await Submit(db, ids, Settings());

            Assert.Equal(AssessmentQuestionnaireSubmitState.Success, result.State);
            var rows = await Messages(pg, ids);
            Assert.Equal(2, rows.Count);
            var confirmation = Assert.Single(rows, r => r.Classification == nameof(EmailTemplateKind.SubmissionConfirmation));
            var notice = Assert.Single(rows, r => r.Classification == nameof(EmailTemplateKind.ConsultantSubmissionNotification));
            Assert.Equal(customer, confirmation.RecipientEmail);
            Assert.Equal(Consultant, notice.RecipientEmail);
            Assert.All(rows, r =>
            {
                Assert.Equal(ids.AssessmentId, r.AssessmentId);
                Assert.Equal(ids.InvitationId, r.InvitationId);
                Assert.Equal(EmailOutboxStatus.Pending, r.Status);
            });
            Assert.Equal(EmailOutboxWriter.SubmissionConfirmationKey(ids.AssessmentId, ids.InvitationId), confirmation.BusinessEventKey);
            Assert.Equal(EmailOutboxWriter.ConsultantNotificationKey(ids.AssessmentId, ids.InvitationId), notice.BusinessEventKey);

            // Same transaction: submission state and outbox rows are both committed.
            await using var verify = pg.CreateDbContext();
            Assert.Equal(AssessmentStatus.Submitted, (await verify.Set<Assessment>().AsNoTracking().SingleAsync(a => a.Id == ids.AssessmentId)).Status);
        }
        finally { await Cleanup(pg, ids); }
    }

    [Fact]
    public async Task Outbox_Rows_Roll_Back_With_Failed_Submission_Transaction()
    {
        var pg = new PostgresIntegrationTestFixture();
        await pg.InitializeAsync();
        var ids = new Ids();
        try
        {
            await using (var seedDb = pg.CreateDbContext())
            {
                await Seed(seedDb, ids, "fail@example.com");
            }

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(pg.ConnectionString)
                .AddInterceptors(new FailCommitInterceptor())
                .Options;
            await using var db = new ApplicationDbContext(options);

            await Assert.ThrowsAnyAsync<Exception>(() => Submit(db, ids, Settings()));

            Assert.Empty(await Messages(pg, ids));
            await using var verify = pg.CreateDbContext();
            Assert.Empty(await verify.Set<AssessmentResponse>().AsNoTracking().Where(r => r.AssessmentId == ids.AssessmentId).ToListAsync());
            Assert.Equal(AssessmentStatus.InProgress, (await verify.Set<Assessment>().AsNoTracking().SingleAsync(a => a.Id == ids.AssessmentId)).Status);
        }
        finally { await Cleanup(pg, ids); }
    }

    [Fact]
    public async Task Validation_Failure_And_Draft_Save_Queue_Nothing()
    {
        var pg = new PostgresIntegrationTestFixture();
        await pg.InitializeAsync();
        var ids = new Ids();
        try
        {
            await using var db = pg.CreateDbContext();
            await Seed(db, ids, "draft@example.com");
            var service = CreateService(db, ids, Settings());

            var invalid = await service.SubmitAsync(ids.InvitationId, "token", "{ not valid", null);
            Assert.NotEqual(AssessmentQuestionnaireSubmitState.Success, invalid.State);

            await service.SaveDraftAsync(ids.InvitationId, "token", ValidPayload(), null);

            Assert.Empty(await Messages(pg, ids));
        }
        finally { await Cleanup(pg, ids); }
    }

    [Fact]
    public async Task Repeated_Submission_Does_Not_Duplicate_Messages()
    {
        var pg = new PostgresIntegrationTestFixture();
        await pg.InitializeAsync();
        var ids = new Ids();
        try
        {
            await using var db = pg.CreateDbContext();
            await Seed(db, ids, "repeat@example.com");

            Assert.Equal(AssessmentQuestionnaireSubmitState.Success, (await Submit(db, ids, Settings())).State);
            db.ChangeTracker.Clear();
            var second = await Submit(db, ids, Settings());

            Assert.NotEqual(AssessmentQuestionnaireSubmitState.Success, second.State);
            Assert.Equal(2, (await Messages(pg, ids)).Count);

            // Direct writer replay (e.g. retry of the same business event) is also idempotent.
            var writer = new EmailOutboxWriter(db, Options.Create(Settings()));
            await writer.EnqueueSubmissionNotificationsAsync(ids.AssessmentId, ids.InvitationId, DateTime.UtcNow);
            Assert.Equal(2, (await Messages(pg, ids)).Count);
        }
        finally { await Cleanup(pg, ids); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    public async Task Missing_Or_Invalid_Consultant_Address_Still_Queues_Customer_Confirmation_And_Logs_Error(string consultant)
    {
        var pg = new PostgresIntegrationTestFixture();
        await pg.InitializeAsync();
        var ids = new Ids();
        try
        {
            await using var db = pg.CreateDbContext();
            await Seed(db, ids, "cust@example.com");
            var logger = new CapturingLogger<EmailOutboxWriter>();

            var result = await Submit(db, ids, Settings(consultant), logger);

            Assert.Equal(AssessmentQuestionnaireSubmitState.Success, result.State);
            var row = Assert.Single(await Messages(pg, ids));
            Assert.Equal(nameof(EmailTemplateKind.SubmissionConfirmation), row.Classification);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("ConsultantNotificationAddress"));
        }
        finally { await Cleanup(pg, ids); }
    }

    [Fact]
    public async Task Email_Failure_Does_Not_Reverse_Committed_Submission_And_Logs_Are_Safe()
    {
        var pg = new PostgresIntegrationTestFixture();
        await pg.InitializeAsync();
        var ids = new Ids();
        try
        {
            await using var db = pg.CreateDbContext();
            await Seed(db, ids, "safe@example.com");
            Assert.Equal(AssessmentQuestionnaireSubmitState.Success, (await Submit(db, ids, Settings())).State);

            var logger = new CapturingLogger<EmailOutboxDispatcher>();
            await using var workerDb = pg.CreateDbContext();
            await new EmailOutboxDispatcher(workerDb, new ThrowingSender(), logger).ProcessDueAsync(10);

            await using var verify = pg.CreateDbContext();
            Assert.Equal(AssessmentStatus.Submitted, (await verify.Set<Assessment>().AsNoTracking().SingleAsync(a => a.Id == ids.AssessmentId)).Status);
            Assert.True(await verify.Set<AssessmentResponse>().AsNoTracking().AnyAsync(r => r.AssessmentId == ids.AssessmentId && !r.IsDraft));
            Assert.All(await Messages(pg, ids), m => Assert.Equal(EmailOutboxStatus.Retry, m.Status));

            var logged = string.Join("\n", logger.Entries.Select(e => e.Message));
            Assert.DoesNotContain("safe@example.com", logged);
            Assert.DoesNotContain("North Plant", logged);
            Assert.DoesNotContain("token", logged, StringComparison.OrdinalIgnoreCase);
        }
        finally { await Cleanup(pg, ids); }
    }

    [Fact]
    public async Task Concurrent_Workers_Cannot_Both_Send_The_Same_Message()
    {
        var pg = new PostgresIntegrationTestFixture();
        await pg.InitializeAsync();
        var ids = new Ids();
        try
        {
            await using (var seedDb = pg.CreateDbContext())
            {
                await Seed(seedDb, ids, "race@example.com");
                var writer = new EmailOutboxWriter(seedDb, Options.Create(Settings(null)));
                await writer.EnqueueSubmissionNotificationsAsync(ids.AssessmentId, ids.InvitationId, DateTime.UtcNow.AddMinutes(-1));
            }

            var sender = new CountingSender();
            await using var db1 = pg.CreateDbContext();
            await using var db2 = pg.CreateDbContext();
            var d1 = new EmailOutboxDispatcher(db1, sender, new CapturingLogger<EmailOutboxDispatcher>());
            var d2 = new EmailOutboxDispatcher(db2, sender, new CapturingLogger<EmailOutboxDispatcher>());

            await Task.WhenAll(d1.ProcessDueAsync(10), d2.ProcessDueAsync(10));

            Assert.Equal(1, sender.Count);
            var row = Assert.Single(await Messages(pg, ids));
            Assert.Equal(EmailOutboxStatus.Sent, row.Status);
            Assert.Equal(1, row.AttemptCount);
            Assert.NotNull(row.ProviderAcceptedUtc);
        }
        finally { await Cleanup(pg, ids); }
    }

    private static AssessmentQuestionnaireService CreateService(ApplicationDbContext db, Ids ids, EmailSettings settings, ILogger<EmailOutboxWriter>? logger = null)
    {
        var access = AssessmentInvitationAccessResult.Authorized(ids.AssessmentId, ids.InvitationId, null, AssessmentInvitationResponseState.NotStarted, false);
        return new AssessmentQuestionnaireService(db, new StubAccess(access), new EmailOutboxWriter(db, Options.Create(settings), logger));
    }

    private static Task<AssessmentQuestionnaireSubmitResult> Submit(ApplicationDbContext db, Ids ids, EmailSettings settings, ILogger<EmailOutboxWriter>? logger = null)
        => CreateService(db, ids, settings, logger).SubmitAsync(ids.InvitationId, "token", ValidPayload(), null);

    private static async Task Seed(ApplicationDbContext db, Ids ids, string customerEmail)
    {
        db.Add(new Assessment
        {
            Id = ids.AssessmentId,
            Status = AssessmentStatus.InProgress,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            Title = "Email workflow",
            SiteName = "Site",
            QuestionnaireVersion = "1.0"
        });
        db.Add(new AssessmentInvitation
        {
            Id = ids.InvitationId,
            AssessmentId = ids.AssessmentId,
            Email = customerEmail,
            TokenHash = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddHours(1)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<List<EmailOutboxMessage>> Messages(PostgresIntegrationTestFixture pg, Ids ids)
    {
        await using var db = pg.CreateDbContext();
        return await db.Set<EmailOutboxMessage>().AsNoTracking().Where(m => m.AssessmentId == ids.AssessmentId).ToListAsync();
    }

    private static async Task Cleanup(PostgresIntegrationTestFixture pg, Ids ids)
    {
        await using var db = pg.CreateDbContext();
        await db.Set<EmailOutboxMessage>().Where(m => m.AssessmentId == ids.AssessmentId).ExecuteDeleteAsync();
        await db.Set<AssessmentResponse>().Where(r => r.AssessmentId == ids.AssessmentId).ExecuteDeleteAsync();
        await db.Set<AssessmentInvitation>().Where(i => i.AssessmentId == ids.AssessmentId).ExecuteDeleteAsync();
        await db.Set<Assessment>().Where(a => a.Id == ids.AssessmentId).ExecuteDeleteAsync();
    }

    private static string ValidPayload() => QuestionnaireResponseV1Serializer.Serialize(new QuestionnaireResponseV1
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
    });

    private sealed class Ids
    {
        public Guid AssessmentId { get; } = Guid.NewGuid();
        public Guid InvitationId { get; } = Guid.NewGuid();
    }

    private sealed class StubAccess : IAssessmentInvitationAccessService
    {
        private readonly AssessmentInvitationAccessResult _result;
        public StubAccess(AssessmentInvitationAccessResult result) => _result = result;
        public Task<AssessmentInvitationAccessResult> ValidateAccessAsync(Guid invitationId, string? invitationToken, CancellationToken ct = default)
            => Task.FromResult(_result);
    }

    private sealed class ThrowingSender : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody) => throw new InvalidOperationException("provider down");
    }

    private sealed class CountingSender : IEmailSender
    {
        private int _count;
        public int Count => _count;

        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody) => SendCore();

        public Task<EmailSendResult> SendAsync(EmailMessage message) => SendCore();

        private async Task<EmailSendResult> SendCore()
        {
            Interlocked.Increment(ref _count);
            await Task.Delay(300);
            return EmailSendResult.Accepted("provider-id");
        }
    }

    /// <summary>Fails the submission transaction at commit time, after the outbox rows were written.</summary>
    private sealed class FailCommitInterceptor : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Injected commit failure.");
    }
}

public class EmailWorkflowTests
{
    [Fact]
    public void Ses_ReplyTo_Defaults_And_Can_Be_Overridden()
    {
        var message = new EmailMessage("c@example.com", "s", "<p>h</p>", "t");

        var defaulted = SesEmailSender.BuildRequest(message, new EmailSettings { FromAddress = "a@sentinelaisystems.net" });
        Assert.Equal("support@sentinelaisystems.net", Assert.Single(defaulted.ReplyToAddresses));

        var overridden = SesEmailSender.BuildRequest(message, new EmailSettings { FromAddress = "a@sentinelaisystems.net", ReplyToAddress = "help@sentinelaisystems.net" });
        Assert.Equal("help@sentinelaisystems.net", Assert.Single(overridden.ReplyToAddresses));
    }

    [Theory]
    [InlineData("good@example.com", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("no-at-sign", false)]
    [InlineData("Name <x@example.com>", false)]
    public void Recipient_Validation(string? address, bool expected)
        => Assert.Equal(expected, EmailOutboxWriter.IsValidAddress(address));

    [Fact]
    public void Backoff_Schedule_Is_Bounded()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), EmailOutboxDispatcher.BackoffFor(1));
        Assert.Equal(TimeSpan.FromMinutes(5), EmailOutboxDispatcher.BackoffFor(2));
        Assert.Equal(TimeSpan.FromMinutes(15), EmailOutboxDispatcher.BackoffFor(3));
        Assert.Equal(TimeSpan.FromMinutes(60), EmailOutboxDispatcher.BackoffFor(4));
        Assert.Equal(TimeSpan.FromMinutes(60), EmailOutboxDispatcher.BackoffFor(9));
    }

    [Fact]
    public async Task Five_Attempt_Limit_Marks_Failed_Without_Creating_New_Business_Events()
    {
        await using var f = await SqliteFixture.CreateAsync();
        var key = "event-1";
        f.Db.Add(new EmailOutboxMessage
        {
            Id = Guid.NewGuid(), BusinessEventKey = key, Classification = "X", RecipientEmail = "c@example.com",
            Subject = "s", HtmlBody = "h", TextBody = "t", NextAttemptUtc = DateTime.UtcNow.AddSeconds(-1), CreatedUtc = DateTime.UtcNow
        });
        await f.Db.SaveChangesAsync();
        var dispatcher = new EmailOutboxDispatcher(f.Db, new AlwaysFailSender(), new CapturingLogger<EmailOutboxDispatcher>());

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(1, await dispatcher.ProcessDueAsync(10));
            var row = await f.Db.Set<EmailOutboxMessage>().AsNoTracking().SingleAsync();
            Assert.Equal(i < 4 ? EmailOutboxStatus.Retry : EmailOutboxStatus.Failed, row.Status);
            await f.Db.Set<EmailOutboxMessage>().ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptUtc, DateTime.UtcNow.AddSeconds(-1)));
        }

        Assert.Equal(0, await dispatcher.ProcessDueAsync(10));
        Assert.Equal(1, await f.Db.Set<EmailOutboxMessage>().CountAsync());
        Assert.Equal(5, await f.Db.Set<EmailOutboxAttempt>().CountAsync());
    }

    [Fact]
    public async Task Provider_Acceptance_Is_Recorded_As_Acceptance_Only()
    {
        await using var f = await SqliteFixture.CreateAsync();
        f.Db.Add(new EmailOutboxMessage
        {
            Id = Guid.NewGuid(), BusinessEventKey = "e", Classification = "X", RecipientEmail = "c@example.com",
            Subject = "s", HtmlBody = "h", TextBody = "t", NextAttemptUtc = DateTime.UtcNow.AddSeconds(-1), CreatedUtc = DateTime.UtcNow
        });
        await f.Db.SaveChangesAsync();

        await new EmailOutboxDispatcher(f.Db, new AcceptingSender(), new CapturingLogger<EmailOutboxDispatcher>()).ProcessDueAsync(10);

        var row = await f.Db.Set<EmailOutboxMessage>().AsNoTracking().SingleAsync();
        Assert.Equal(EmailOutboxStatus.Sent, row.Status);
        Assert.NotNull(row.ProviderAcceptedUtc);
        Assert.Equal("ses-id", row.ProviderMessageId);
        Assert.Equal("ProviderAccepted", (await f.Db.Set<EmailOutboxAttempt>().AsNoTracking().SingleAsync()).Outcome);
    }

    [Fact]
    public async Task Invitation_Uses_Branded_Template_With_Valid_Token_And_Replacement_Revokes_Previous()
    {
        await using var f = await SqliteFixture.CreateAsync();
        var assessmentId = Guid.NewGuid();
        f.Db.Add(new Assessment
        {
            Id = assessmentId, Title = "A", SiteName = "S", ContactEmail = "customer@example.com",
            Status = AssessmentStatus.Draft, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        });
        await f.Db.SaveChangesAsync();
        var sender = new CapturingSender();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AppUrls:Base"] = "https://sentinelaisystems.net" }).Build();
        var issuance = new AssessmentInvitationIssuanceService(f.Db);
        var delivery = new AssessmentInvitationDeliveryService(f.Db, sender, config);
        var access = new AssessmentInvitationAccessService(f.Db);

        var first = await new AssessmentInvitationSendService(f.Db, issuance, delivery).SendInvitationAsync(assessmentId);

        Assert.True(first.Succeeded);
        var m1 = Assert.Single(sender.Messages);
        Assert.Equal("customer@example.com", m1.ToEmail);
        Assert.Contains("Sentinel AI Systems", m1.HtmlBody);
        Assert.Contains("Start your assessment", m1.HtmlBody);
        Assert.False(string.IsNullOrWhiteSpace(m1.TextBody));
        Assert.Equal(nameof(EmailTemplateKind.AssessmentInvitation), m1.Classification);
        var token1 = ExtractToken(m1.TextBody!);
        Assert.True((await access.ValidateAccessAsync(first.InvitationId!.Value, token1)).IsAuthorized);
        var inv1 = await f.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(i => i.Id == first.InvitationId.Value);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Accepted, inv1.DeliveryStatus);
        Assert.Equal(1, inv1.DeliveryAttemptCount);

        // Existing lifecycle: only undelivered invitations are replaceable.
        await f.Db.Set<AssessmentInvitation>().Where(i => i.Id == first.InvitationId.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.DeliveryStatus, AssessmentInvitationDeliveryStatus.Failed));

        var replaced = await new AssessmentInvitationReplacementSendService(f.Db, issuance, delivery).ReplaceAndSendAsync(assessmentId, first.InvitationId.Value);

        Assert.True(replaced.Succeeded);
        Assert.Equal(2, sender.Messages.Count);
        var token2 = ExtractToken(sender.Messages[1].TextBody!);
        Assert.NotEqual(token1, token2);
        Assert.False((await access.ValidateAccessAsync(first.InvitationId.Value, token1)).IsAuthorized);
        Assert.True((await access.ValidateAccessAsync(replaced.InvitationId!.Value, token2)).IsAuthorized);
        var inv2 = await f.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(i => i.Id == replaced.InvitationId.Value);
        Assert.Equal(AssessmentInvitationDeliveryStatus.Accepted, inv2.DeliveryStatus);
        Assert.Equal(1, inv2.DeliveryAttemptCount);
    }

    [Fact]
    public async Task Development_Suppression_Leaves_Invitation_Pending_And_Does_Not_Mark_Accepted()
    {
        await using var f = await SqliteFixture.CreateAsync();
        var assessmentId = Guid.NewGuid();
        f.Db.Add(new Assessment
        {
            Id = assessmentId, Title = "A", ContactEmail = "customer@example.com",
            Status = AssessmentStatus.Draft, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        });
        await f.Db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AppUrls:Base"] = "https://sentinelaisystems.net" }).Build();
        var service = new AssessmentInvitationSendService(f.Db, new AssessmentInvitationIssuanceService(f.Db),
            new AssessmentInvitationDeliveryService(f.Db, new DevelopmentSuppressingEmailSender(), config));

        await service.SendInvitationAsync(assessmentId);

        var inv = await f.Db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync();
        Assert.NotEqual(AssessmentInvitationDeliveryStatus.Accepted, inv.DeliveryStatus);
        Assert.Null(inv.DeliveryAcceptedUtc);
    }

    private static string ExtractToken(string text)
    {
        var match = Regex.Match(text, @"token=([^&\s""'<]+)");
        Assert.True(match.Success);
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    private sealed class CapturingSender : IEmailSender
    {
        public List<EmailMessage> Messages { get; } = new();

        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
            => SendAsync(new EmailMessage(toEmail, subject, htmlBody));

        public Task<EmailSendResult> SendAsync(EmailMessage message)
        {
            Messages.Add(message);
            return Task.FromResult(EmailSendResult.Accepted());
        }
    }

    private sealed class AlwaysFailSender : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody) => throw new InvalidOperationException("fail");
    }

    private sealed class AcceptingSender : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody) => Task.FromResult(EmailSendResult.Accepted("ses-id"));
    }

    private sealed class SqliteFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ApplicationDbContext Db { get; }

        private SqliteFixture(SqliteConnection c, ApplicationDbContext db) { _connection = c; Db = db; }

        public static async Task<SqliteFixture> CreateAsync()
        {
            var c = new SqliteConnection("Data Source=:memory:");
            await c.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(c).Options);
            await db.Database.EnsureCreatedAsync();
            return new SqliteFixture(c, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception) + (exception?.ToString() ?? string.Empty)));
}
