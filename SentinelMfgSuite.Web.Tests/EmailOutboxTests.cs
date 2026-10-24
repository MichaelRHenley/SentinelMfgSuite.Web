using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Email;
using SentinelMfgSuite.Web.Services;
using SentinelMfgSuite.Web.Services.Email;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public class EmailOutboxTests
{
    private static readonly EmailSettings Settings = new()
    {
        FromAddress = "noreply@sentinelaisystems.net",
        ConsultantNotificationAddress = "consultant@sentinelaisystems.net"
    };

    [Fact]
    public void Templates_Render_Html_And_PlainText()
    {
        var r = BrandedEmailTemplates.AssessmentInvitation("https://x.test/a?token=abc&b=1", new DateTime(2030, 1, 2, 3, 4, 0, DateTimeKind.Utc));

        Assert.Contains("Sentinel AI Systems", r.HtmlBody);
        Assert.Contains("Start your assessment", r.HtmlBody);
        Assert.Contains("token=abc&amp;b=1", r.HtmlBody);
        Assert.Contains("https://x.test/a?token=abc&b=1", r.TextBody);
        Assert.Contains("support@sentinelaisystems.net", r.TextBody);
        Assert.DoesNotContain("<", r.TextBody);
    }

    [Fact]
    public void Payment_And_Report_Templates_Are_Inactive()
    {
        Assert.False(BrandedEmailTemplates.IsActive(EmailTemplateKind.PaymentConfirmation));
        Assert.False(BrandedEmailTemplates.IsActive(EmailTemplateKind.ReportReady));
        Assert.False(BrandedEmailTemplates.IsActive(EmailTemplateKind.CorrectedReportNotification));
        Assert.True(BrandedEmailTemplates.IsActive(EmailTemplateKind.SubmissionConfirmation));
    }

    [Fact]
    public void Ses_Request_Has_DisplayName_ReplyTo_Html_And_Text()
    {
        var req = SesEmailSender.BuildRequest(new EmailMessage("c@example.com", "S", "<p>h</p>", "t"), new EmailSettings { FromAddress = "noreply@sentinelaisystems.net" });

        Assert.Equal("\"Sentinel AI Systems\" <noreply@sentinelaisystems.net>", req.FromEmailAddress);
        Assert.Equal("support@sentinelaisystems.net", Assert.Single(req.ReplyToAddresses));
        Assert.Equal("<p>h</p>", req.Content.Simple.Body.Html.Data);
        Assert.Equal("t", req.Content.Simple.Body.Text.Data);
    }

    [Fact]
    public async Task Writer_Is_Idempotent_Per_Submission_Event()
    {
        await using var f = await Fixture.CreateAsync();
        var a = Guid.NewGuid();
        var i = Guid.NewGuid();
        var writer = new EmailOutboxWriter(f.Db, Options.Create(Settings));

        await writer.EnqueueSubmissionNotificationsAsync(a, i, DateTime.UtcNow);
        await writer.EnqueueSubmissionNotificationsAsync(a, i, DateTime.UtcNow);

        var rows = await f.Db.Set<EmailOutboxMessage>().AsNoTracking().ToListAsync();
        Assert.Single(rows, m => m.Classification == nameof(EmailTemplateKind.ConsultantSubmissionNotification));
        Assert.All(rows, m => Assert.Equal(a, m.AssessmentId));
    }

    [Fact]
    public async Task Dispatcher_Marks_Sent_On_Provider_Acceptance()
    {
        await using var f = await Fixture.CreateAsync();
        await Seed(f.Db);
        var sender = new FakeSender(EmailSendResult.Accepted("mid-1"));

        var n = await new EmailOutboxDispatcher(f.Db, sender, NullLogger<EmailOutboxDispatcher>.Instance).ProcessDueAsync(10);

        Assert.Equal(1, n);
        var m = await f.Db.Set<EmailOutboxMessage>().AsNoTracking().SingleAsync();
        Assert.Equal(EmailOutboxStatus.Sent, m.Status);
        Assert.Equal("mid-1", m.ProviderMessageId);
        Assert.Equal(1, await f.Db.Set<EmailOutboxAttempt>().CountAsync());
        Assert.Equal("Reply", sender.Last!.Classification);
    }

    [Fact]
    public async Task Dispatcher_Retries_With_Backoff_Then_Fails_At_Max()
    {
        await using var f = await Fixture.CreateAsync();
        await Seed(f.Db, maxAttempts: 2);
        var sender = new FakeSender(null);
        var d = new EmailOutboxDispatcher(f.Db, sender, NullLogger<EmailOutboxDispatcher>.Instance);

        await d.ProcessDueAsync(10);
        var m = await f.Db.Set<EmailOutboxMessage>().AsNoTracking().SingleAsync();
        Assert.Equal(EmailOutboxStatus.Retry, m.Status);
        Assert.True(m.NextAttemptUtc > DateTime.UtcNow);
        Assert.Equal(0, await d.ProcessDueAsync(10));

        await f.Db.Set<EmailOutboxMessage>().ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptUtc, DateTime.UtcNow.AddSeconds(-1)));
        await d.ProcessDueAsync(10);

        m = await f.Db.Set<EmailOutboxMessage>().AsNoTracking().SingleAsync();
        Assert.Equal(EmailOutboxStatus.Failed, m.Status);
        Assert.Equal(2, await f.Db.Set<EmailOutboxAttempt>().CountAsync());
    }

    [Fact]
    public async Task Dispatcher_Recovers_Expired_Processing_Lock()
    {
        await using var f = await Fixture.CreateAsync();
        await Seed(f.Db);
        await f.Db.Set<EmailOutboxMessage>().ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, EmailOutboxStatus.Processing)
            .SetProperty(x => x.LockedUntilUtc, DateTime.UtcNow.AddMinutes(-1)));

        var n = await new EmailOutboxDispatcher(f.Db, new FakeSender(EmailSendResult.Accepted("m")), NullLogger<EmailOutboxDispatcher>.Instance).ProcessDueAsync(10);

        Assert.Equal(1, n);
    }

    [Fact]
    public async Task Development_Sender_Suppresses_And_Outbox_Records_Suppressed()
    {
        await using var f = await Fixture.CreateAsync();
        await Seed(f.Db);

        await new EmailOutboxDispatcher(f.Db, new DevelopmentSuppressingEmailSender(), NullLogger<EmailOutboxDispatcher>.Instance).ProcessDueAsync(10);

        var m = await f.Db.Set<EmailOutboxMessage>().AsNoTracking().SingleAsync();
        Assert.Equal(EmailOutboxStatus.Suppressed, m.Status);
        Assert.Null(m.ProviderAcceptedUtc);
    }

    private static async Task Seed(ApplicationDbContext db, int maxAttempts = 5)
    {
        db.Add(new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            BusinessEventKey = Guid.NewGuid().ToString(),
            Classification = "Reply",
            RecipientEmail = "c@example.com",
            Subject = "s",
            HtmlBody = "<p>h</p>",
            TextBody = "t",
            MaxAttempts = maxAttempts,
            NextAttemptUtc = DateTime.UtcNow.AddSeconds(-1),
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private sealed class FakeSender : IEmailSender
    {
        private readonly EmailSendResult? _result;
        public EmailMessage? Last { get; private set; }

        public FakeSender(EmailSendResult? result) => _result = result;

        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
            => SendAsync(new EmailMessage(toEmail, subject, htmlBody));

        public Task<EmailSendResult> SendAsync(EmailMessage message)
        {
            Last = message;
            return _result is null ? throw new InvalidOperationException("boom") : Task.FromResult(_result);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ApplicationDbContext Db { get; }

        private Fixture(SqliteConnection c, ApplicationDbContext db) { _connection = c; Db = db; }

        public static async Task<Fixture> CreateAsync()
        {
            var c = new SqliteConnection("Data Source=:memory:");
            await c.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(c).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(c, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
