using System.ComponentModel.DataAnnotations;

namespace SentinelMfgSuite.Web.Models
{
    public class LaunchListForm
    {
        [Required]
        public string Name { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [Display(Name = "Work email")]
        public string Email { get; set; } = string.Empty;

        public string? Company { get; set; }

        [Display(Name = "What are you hoping Sentinel could help with?")]
        public string? Notes { get; set; }
    }
}


