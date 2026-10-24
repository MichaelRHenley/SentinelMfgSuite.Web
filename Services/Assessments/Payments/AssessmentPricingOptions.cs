using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Services.Assessments.Payments;

/// <summary>Configuration section "AssessmentPricing". No price is hard-coded in the domain model.</summary>
public sealed class AssessmentPricingOptions
{
    public const string SectionName = "AssessmentPricing";

    public string Currency { get; set; } = "CAD";

    /// <summary>Price before tax. Must be configured (greater than zero) before orders can be created.</summary>
    public decimal BasePrice { get; set; }

    /// <summary>Tax is NOT added unless explicitly configured as Exclusive.</summary>
    public AssessmentTaxTreatment TaxTreatment { get; set; } = AssessmentTaxTreatment.None;

    /// <summary>Fractional rate (e.g. 0.05 for 5%). Used only when TaxTreatment is Exclusive.</summary>
    public decimal TaxRate { get; set; }

    public string? TaxLabel { get; set; }

    /// <summary>When true, newly created assessments require a verified paid order before invitation.</summary>
    public bool EnforcePayment { get; set; } = true;

    /// <summary>Returns null when valid, otherwise a non-sensitive description. TaxTreatment None is provisional configuration, not a legal determination.</summary>
    public string? Validate()
    {
        if (!string.Equals(Currency, "CAD", StringComparison.OrdinalIgnoreCase))
            return "AssessmentPricing:Currency must be CAD.";
        if (BasePrice <= 0 || decimal.Round(BasePrice, 2) != BasePrice)
            return "AssessmentPricing:BasePrice must be greater than zero with at most two decimal places.";
        if (TaxRate < 0 || TaxRate >= 1)
            return "AssessmentPricing:TaxRate must be at least 0 and less than 1.";
        if (TaxTreatment == AssessmentTaxTreatment.None && TaxRate != 0)
            return "AssessmentPricing:TaxRate must be 0 when TaxTreatment is None.";
        if (TaxTreatment == AssessmentTaxTreatment.Exclusive && TaxRate <= 0)
            return "AssessmentPricing:TaxRate must be greater than 0 when TaxTreatment is Exclusive.";
        return null;
    }
}
