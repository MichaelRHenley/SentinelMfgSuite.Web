using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentinelMfgSuite.Web.Data;

namespace SentinelMfgSuite.Web.Tests.Infrastructure;

public sealed class PostgresIntegrationTestFixture
{
    public const string ConnectionEnvironmentVariable = "SENTINEL_POSTGRES_TEST_CONNECTION";

    private readonly NpgsqlConnectionStringBuilder _connectionStringBuilder;

    public string ConnectionString { get; }

    public PostgresIntegrationTestFixture()
    {
        var rawConnection = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(rawConnection))
        {
            throw new InvalidOperationException(
                $"Missing required environment variable '{ConnectionEnvironmentVariable}' for PostgreSQL integration tests.");
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(rawConnection);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("PostgreSQL integration test connection string is invalid.", ex);
        }

        ValidateBuilder(builder);

        _connectionStringBuilder = builder;
        ConnectionString = builder.ConnectionString;
    }

    public ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new ApplicationDbContext(options);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var db = CreateDbContext();
        await db.Database.MigrateAsync(ct);
    }

    private static void ValidateBuilder(NpgsqlConnectionStringBuilder builder)
    {
        var host = (builder.Host ?? string.Empty).Trim();
        var dockerNetworkHost = host.Equals("postgres", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Environment.GetEnvironmentVariable("SENTINEL_POSTGRES_TEST_ALLOW_DOCKER_HOST"), "true", StringComparison.OrdinalIgnoreCase);
        if (!dockerNetworkHost
            && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PostgreSQL integration tests are only allowed against localhost, 127.0.0.1, or the explicitly enabled isolated Docker host 'postgres'.");
        }

        var database = (builder.Database ?? string.Empty).Trim();
        if (!database.Equals("sentinel_integration_test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PostgreSQL integration tests must target database 'sentinel_integration_test'.");
        }

        if (string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new InvalidOperationException("PostgreSQL integration tests require a nonempty username.");
        }
    }
}
