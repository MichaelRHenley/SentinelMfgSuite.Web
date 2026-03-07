// Models/CustomBuildForm.cs
using System.ComponentModel.DataAnnotations;

namespace SentinelMfgSuite.Web.Models
{
    public class CustomBuildForm
    {
        [Required]
        [Display(Name = "Your name")]
        public string Name { get; set; } = string.Empty;

        [Required, EmailAddress]
        [Display(Name = "Work email")]
        public string Email { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Company or site")]
        public string Company { get; set; } = string.Empty;

        [Required]
        [Display(Name = "What are you hoping to build?")]
        public string Notes { get; set; } = string.Empty;

        // New: where did they come from / what CTA?
        public string Topic { get; set; } = "CustomBuild";
       
     
        public string? Role { get; set; }


        // tracking/meta
        public string? SourcePage { get; set; }
        public string? Referrer { get; set; }

        public string? UtmSource { get; set; }
        public string? UtmMedium { get; set; }
        public string? UtmCampaign { get; set; }
        public string? UtmContent { get; set; }
        public string? UtmTerm { get; set; }
    }
}

