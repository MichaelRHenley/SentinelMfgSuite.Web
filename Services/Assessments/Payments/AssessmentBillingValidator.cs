using System.ComponentModel.DataAnnotations;

namespace SentinelMfgSuite.Web.Services.Assessments.Payments;

public static class AssessmentBillingValidator
{
    public const string OrganizationMessage = "Customer legal organization name is required.";
    public const string ContactNameMessage = "Billing contact name is required.";
    public const string EmailRequiredMessage = "Billing contact email is required.";
    public const string EmailInvalidMessage = "Billing contact email must be a valid email address.";

    /// <summary>Returns field-name to message pairs; empty when valid.</summary>
    public static IReadOnlyList<(string Field, string Message)> Validate(string? organization, string? contactName, string? contactEmail)
    {
        var errors = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(organization)) errors.Add(("CustomerOrganization", OrganizationMessage));
        else if (organization.Trim().Length > 200) errors.Add(("CustomerOrganization", "Customer organization must be 200 characters or fewer."));

        if (string.IsNullOrWhiteSpace(contactName)) errors.Add(("BillingContactName", ContactNameMessage));
        else if (contactName.Trim().Length > 200) errors.Add(("BillingContactName", "Billing contact name must be 200 characters or fewer."));

        if (string.IsNullOrWhiteSpace(contactEmail)) errors.Add(("BillingContactEmail", EmailRequiredMessage));
        else
        {
            var e = contactEmail.Trim();
            if (e.Length > 320 || !new EmailAddressAttribute().IsValid(e)) errors.Add(("BillingContactEmail", EmailInvalidMessage));
        }
        return errors;
    }
}
