using System.ComponentModel.DataAnnotations;

namespace SentinelMfgSuite.Web.Models
{
    public class CustomBuildInquiryViewModel
    {
        [Required]
        public string Name { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [Display(Name = "Work email")]
        public string WorkEmail { get; set; } = string.Empty;

        public string? Company { get; set; }

        [Required]
        [Display(Name = "What are you looking to improve?")]
        public string LookingToImprove { get; set; } = string.Empty;
    }
}

