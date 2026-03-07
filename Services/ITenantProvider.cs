namespace SentinelMfgSuite.Web.Services;

public interface ITenantProvider
{
    Task<Guid> GetTenantIdAsync(CancellationToken ct = default);
}



