namespace SentinelMfgSuite.Web.Services
{
    public class EmailSettings
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public bool EnableSsl { get; set; }
        public string FromAddress { get; set; } = string.Empty;
        public string FromName { get; set; } = "Sentinel AI Systems";
        public string ReplyToAddress { get; set; } = "support@sentinelaisystems.net";
        public string ConsultantNotificationAddress { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}

