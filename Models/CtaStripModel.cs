namespace SentinelMfgSuite.Web.Models
{
    public class CtaStripModel
    {
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";

        public string PrimaryText { get; set; } = "Book a demo";
        public string PrimaryAction { get; set; } = "Contact";
        public string PrimaryController { get; set; } = "Home";

        public string? SecondaryText { get; set; }
        public string? SecondaryAction { get; set; }
        public string? SecondaryController { get; set; }
    }
}

