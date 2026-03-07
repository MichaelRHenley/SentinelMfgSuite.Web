namespace SentinelMfgSuite.Web.Models
{
    public class TierCardViewModel
    {
        public string TierLabel { get; set; } = string.Empty;
        public string? TierBadge { get; set; }

        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        public decimal MonthlyPrice { get; set; }
        public decimal? AnnualPrice { get; set; }

        public string ButtonText { get; set; } = string.Empty;
        public string ButtonController { get; set; } = "Home";
        public string ButtonAction { get; set; } = "Contact";

        public bool Primary { get; set; }
        public string? CssModifier { get; set; }

        public IEnumerable<string> Features { get; set; } = Enumerable.Empty<string>();

        // Single property for the topic slug
        public string? ButtonRouteTopic { get; set; }
    }
}
