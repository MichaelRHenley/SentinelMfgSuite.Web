using Xunit;

namespace SentinelMfgSuite.Web.Tests.Infrastructure;

/// <summary>
/// Serializes all PostgreSQL integration tests so concurrent MigrateAsync calls and
/// shared-database state cannot race.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresIntegrationCollection
{
    public const string Name = "PostgresIntegration";
}
