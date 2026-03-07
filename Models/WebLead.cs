using System;

namespace SentinelMfgSuite.Web.Models
{
    public class WebLead
    {
        public int Id { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        // Contact core
        public string? Name { get; set; }
        public string Email { get; set; } = string.Empty;
        public string? Company { get; set; }
        public string? Role { get; set; }
        public string? Topic { get; set; }   // LaunchList, PilotRollout, PricingConversation, etc.
        public string? Message { get; set; }

        // Page / source meta
        public string? SourcePage { get; set; }
        public string? Referrer { get; set; }

        // UTM
        public string? UtmSource { get; set; }
        public string? UtmMedium { get; set; }
        public string? UtmCampaign { get; set; }
        public string? UtmContent { get; set; }
        public string? UtmTerm { get; set; }
    }
}

