using Amazon;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Options;

namespace SentinelMfgSuite.Web.Services
{
    public class SesEmailSender : IEmailSender
    {
        private readonly EmailSettings _settings;
        private readonly IAmazonSimpleEmailServiceV2 _ses;

        public SesEmailSender(IOptions<EmailSettings> options)
            : this(options, new AmazonSimpleEmailServiceV2Client(RegionEndpoint.CACentral1))
        {
        }

        public SesEmailSender(IOptions<EmailSettings> options, IAmazonSimpleEmailServiceV2 ses)
        {
            _settings = options.Value;
            _ses = ses;
        }

        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
            => SendAsync(new EmailMessage(toEmail, subject, htmlBody));

        public async Task<EmailSendResult> SendAsync(EmailMessage message)
        {
            var response = await _ses.SendEmailAsync(BuildRequest(message, _settings));
            return EmailSendResult.Accepted(response?.MessageId);
        }

        public static SendEmailRequest BuildRequest(EmailMessage message, EmailSettings settings)
        {
            var from = string.IsNullOrWhiteSpace(settings.FromName)
                ? settings.FromAddress
                : $"\"{settings.FromName.Replace("\"", string.Empty)}\" <{settings.FromAddress}>";
            var replyTo = message.ReplyToAddress ?? settings.ReplyToAddress;
            var toEmail = message.ToEmail;
            var subject = message.Subject;
            var htmlBody = message.HtmlBody;
            var request = new SendEmailRequest
            {
                FromEmailAddress = from,

                Destination = new Destination
                {
                    ToAddresses = new List<string> { toEmail }
                },

                Content = new EmailContent
                {
                    Simple = new Message
                    {
                        Subject = new Content
                        {
                            Data = subject,
                            Charset = "UTF-8"
                        },

                        Body = new Body
                        {
                            Html = new Content
                            {
                                Data = htmlBody,
                                Charset = "UTF-8"
                            },
                            Text = string.IsNullOrWhiteSpace(message.TextBody)
                                ? null
                                : new Content { Data = message.TextBody, Charset = "UTF-8" }
                        }
                    }
                }
            };

            if (!string.IsNullOrWhiteSpace(replyTo))
            {
                request.ReplyToAddresses = new List<string> { replyTo };
            }

            return request;
        }
    }
}
