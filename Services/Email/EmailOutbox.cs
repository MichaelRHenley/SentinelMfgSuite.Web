using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Email;

namespace SentinelMfgSuite.Web.Services.Email
{
    public interface IEmailOutboxWriter
    {
        /// <summary>
        /// Stages and saves the customer confirmation and consultant notification on the current
        /// DbContext (and therefore its ambient transaction). Idempotent per assessment/invitation.
        /// </summary>
        Task EnqueueSubmissionNotificationsAsync(Guid assessmentId, Guid invitationId, DateTime submittedUtc, CancellationToken ct = default);
    }

    public sealed class EmailOutboxWriter : IEmailOutboxWriter
    {
        private readonly ApplicationDbContext _db;
        private readonly EmailSettings _settings;
        private readonly ILogger<EmailOutboxWriter>? _logger;

        public EmailOutboxWriter(ApplicationDbContext db, IOptions<EmailSettings> options, ILogger<EmailOutboxWriter>? logger = null)
        {
            _db = db;
            _settings = options.Value;
            _logger = logger;
        }

        public static bool IsValidAddress(string? address)
            => !string.IsNullOrWhiteSpace(address)
               && System.Net.Mail.MailAddress.TryCreate(address.Trim(), out var parsed)
               && string.Equals(parsed.Address, address.Trim(), StringComparison.OrdinalIgnoreCase);

        public static string SubmissionConfirmationKey(Guid assessmentId, Guid invitationId) => $"submission-confirmation:{assessmentId:N}:{invitationId:N}";
        public static string ConsultantNotificationKey(Guid assessmentId, Guid invitationId) => $"consultant-submission:{assessmentId:N}:{invitationId:N}";

        public async Task EnqueueSubmissionNotificationsAsync(Guid assessmentId, Guid invitationId, DateTime submittedUtc, CancellationToken ct = default)
        {
            var assessment = await _db.Set<Assessment>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == assessmentId, ct);
            var invitationEmail = await _db.Set<AssessmentInvitation>().AsNoTracking()
                .Where(i => i.Id == invitationId).Select(i => i.Email).SingleOrDefaultAsync(ct);

            var customer = !string.IsNullOrWhiteSpace(invitationEmail) ? invitationEmail : assessment?.ContactEmail;
            var added = false;

            if (!IsValidAddress(customer))
            {
                _logger?.LogError("Submission confirmation not queued: customer recipient address missing or invalid. AssessmentId={AssessmentId}", assessmentId);
            }
            else
            {
                added |= await StageAsync(SubmissionConfirmationKey(assessmentId, invitationId),
                    BrandedEmailTemplates.SubmissionConfirmation(), customer!.Trim(), assessmentId, invitationId, submittedUtc, ct);
            }

            if (!IsValidAddress(_settings.ConsultantNotificationAddress))
            {
                _logger?.LogError("Consultant notification not queued: Email:ConsultantNotificationAddress is missing or invalid. AssessmentId={AssessmentId}", assessmentId);
            }
            else
            {
                added |= await StageAsync(ConsultantNotificationKey(assessmentId, invitationId),
                    BrandedEmailTemplates.ConsultantSubmissionNotification(assessmentId, assessment?.SiteName, assessment?.Title, submittedUtc),
                    _settings.ConsultantNotificationAddress.Trim(), assessmentId, invitationId, submittedUtc, ct);
            }

            if (added)
            {
                await _db.SaveChangesAsync(ct);
            }
        }

        private async Task<bool> StageAsync(string key, RenderedEmail rendered, string recipient, Guid assessmentId, Guid invitationId, DateTime nowUtc, CancellationToken ct)
        {
            if (await _db.Set<EmailOutboxMessage>().AnyAsync(m => m.BusinessEventKey == key, ct))
            {
                return false;
            }

            _db.Add(new EmailOutboxMessage
            {
                Id = Guid.NewGuid(),
                BusinessEventKey = key,
                Classification = rendered.Classification,
                AssessmentId = assessmentId,
                InvitationId = invitationId,
                RecipientEmail = recipient,
                Subject = rendered.Subject,
                HtmlBody = rendered.HtmlBody,
                TextBody = rendered.TextBody,
                Status = EmailOutboxStatus.Pending,
                NextAttemptUtc = nowUtc,
                CreatedUtc = nowUtc
            });
            return true;
        }
    }

    public interface IEmailOutboxDispatcher
    {
        Task<int> ProcessDueAsync(int batchSize, CancellationToken ct = default);
    }

    public sealed class EmailOutboxDispatcher : IEmailOutboxDispatcher
    {
        public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)
        };

        private readonly ApplicationDbContext _db;
        private readonly IEmailSender _sender;
        private readonly ILogger<EmailOutboxDispatcher> _logger;

        public EmailOutboxDispatcher(ApplicationDbContext db, IEmailSender sender, ILogger<EmailOutboxDispatcher> logger)
        {
            _db = db;
            _sender = sender;
            _logger = logger;
        }

        public static TimeSpan BackoffFor(int attemptCount)
            => Backoff[Math.Clamp(attemptCount - 1, 0, Backoff.Length - 1)];

        public async Task<int> ProcessDueAsync(int batchSize, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var ids = await _db.Set<EmailOutboxMessage>().AsNoTracking()
                .Where(m => ((m.Status == EmailOutboxStatus.Pending || m.Status == EmailOutboxStatus.Retry) && m.NextAttemptUtc <= now)
                    || (m.Status == EmailOutboxStatus.Processing && m.LockedUntilUtc != null && m.LockedUntilUtc <= now))
                .OrderBy(m => m.NextAttemptUtc)
                .Select(m => m.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            var processed = 0;
            foreach (var id in ids)
            {
                if (await ProcessOneAsync(id, ct))
                {
                    processed++;
                }
            }

            return processed;
        }

        private async Task<bool> ProcessOneAsync(Guid id, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var lockedUntil = now.Add(LockDuration);
            var claimed = await _db.Set<EmailOutboxMessage>()
                .Where(m => m.Id == id
                    && (((m.Status == EmailOutboxStatus.Pending || m.Status == EmailOutboxStatus.Retry) && m.NextAttemptUtc <= now)
                        || (m.Status == EmailOutboxStatus.Processing && m.LockedUntilUtc != null && m.LockedUntilUtc <= now)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, EmailOutboxStatus.Processing)
                    .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1)
                    .SetProperty(m => m.LockedUntilUtc, lockedUntil), ct);

            if (claimed != 1)
            {
                return false;
            }

            _db.ChangeTracker.Clear();
            var message = await _db.Set<EmailOutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == id, ct);
            var attempt = new EmailOutboxAttempt
            {
                Id = Guid.NewGuid(),
                OutboxMessageId = id,
                AttemptNumber = message.AttemptCount,
                StartedUtc = now
            };

            // A crash after claim leaves the row Processing; it is re-claimed once the lock expires.
            if (message.AttemptCount > message.MaxAttempts)
            {
                attempt.CompletedUtc = DateTime.UtcNow;
                attempt.Outcome = "Abandoned";
                attempt.FailureCode = "MaxAttemptsExceeded";
                await FinishAsync(message, attempt, EmailOutboxStatus.Failed, null, "MaxAttemptsExceeded", null, ct);
                return true;
            }

            try
            {
                var result = await _sender.SendAsync(new EmailMessage(
                    message.RecipientEmail, message.Subject, message.HtmlBody, message.TextBody, message.Classification));

                attempt.CompletedUtc = DateTime.UtcNow;
                if (result.Outcome == EmailSendOutcome.Suppressed)
                {
                    attempt.Outcome = "Suppressed";
                    await FinishAsync(message, attempt, EmailOutboxStatus.Suppressed, null, "EmailSuppressed", null, ct);
                }
                else
                {
                    attempt.Outcome = "ProviderAccepted";
                    await FinishAsync(message, attempt, EmailOutboxStatus.Sent, result.ProviderMessageId, null, null, ct);
                }
            }
            catch (Exception ex)
            {
                // Log type and classification only; never recipient, body or tokens.
                _logger.LogWarning("Email outbox send failed. Classification={Classification} Attempt={Attempt} ErrorType={ErrorType}",
                    message.Classification, message.AttemptCount, ex.GetType().Name);

                attempt.CompletedUtc = DateTime.UtcNow;
                attempt.Outcome = "Failed";
                attempt.FailureCode = "EmailProviderFailure";
                var terminal = message.AttemptCount >= message.MaxAttempts;
                await FinishAsync(message, attempt,
                    terminal ? EmailOutboxStatus.Failed : EmailOutboxStatus.Retry,
                    null, "EmailProviderFailure",
                    terminal ? null : DateTime.UtcNow.Add(BackoffFor(message.AttemptCount)), ct);
            }

            return true;
        }

        private async Task FinishAsync(EmailOutboxMessage message, EmailOutboxAttempt attempt, EmailOutboxStatus status,
            string? providerMessageId, string? failureCode, DateTime? nextAttemptUtc, CancellationToken ct)
        {
            DateTime? accepted = status == EmailOutboxStatus.Sent ? DateTime.UtcNow : null;
            var next = nextAttemptUtc ?? message.NextAttemptUtc;

            await _db.Set<EmailOutboxMessage>()
                .Where(m => m.Id == message.Id && m.Status == EmailOutboxStatus.Processing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, status)
                    .SetProperty(m => m.LockedUntilUtc, (DateTime?)null)
                    .SetProperty(m => m.NextAttemptUtc, next)
                    .SetProperty(m => m.ProviderAcceptedUtc, accepted)
                    .SetProperty(m => m.ProviderMessageId, providerMessageId)
                    .SetProperty(m => m.LastFailureCode, failureCode), ct);

            _db.Add(attempt);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }
    }

    public sealed class EmailOutboxHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<EmailOutboxHostedService> _logger;

        public EmailOutboxHostedService(IServiceScopeFactory scopes, ILogger<EmailOutboxHostedService> logger)
        {
            _scopes = scopes;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var dispatcher = scope.ServiceProvider.GetRequiredService<IEmailOutboxDispatcher>();
                    await dispatcher.ProcessDueAsync(20, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Email outbox cycle failed. ErrorType={ErrorType}", ex.GetType().Name);
                }

                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
