using Microsoft.Extensions.Options;

namespace SentinelMfgSuite.Web.Services.Assessments.Payments;

public sealed class AssessmentPricingOptionsValidator : IValidateOptions<AssessmentPricingOptions>
{
    public ValidateOptionsResult Validate(string? name, AssessmentPricingOptions options)
    {
        if (!options.EnforcePayment) return ValidateOptionsResult.Success;
        var error = options.Validate();
        return error is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
    }
}
