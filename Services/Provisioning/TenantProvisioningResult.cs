namespace SentinelMfgSuite.Web.Services.Provisioning;

public record TenantProvisioningResult(
    Guid TenantId,
    string UserId,
    bool Created);

