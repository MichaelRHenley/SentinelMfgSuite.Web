using Npgsql;
using Xunit;

namespace SentinelMfgSuite.Web.Tests.Infrastructure;

public class PostgresIntegrationTestFixtureTests
{
    [Fact]
    public void Constructor_Allows_Valid_Local_Configuration()
    {
        using var env = TestEnvironmentVariableScope.Set(
            PostgresIntegrationTestFixture.ConnectionEnvironmentVariable,
            "Host=127.0.0.1;Port=5432;Database=sentinel_integration_test;Username=sentinel_test;Password=not-used");

        var fixture = new PostgresIntegrationTestFixture();

        Assert.False(string.IsNullOrWhiteSpace(fixture.ConnectionString));
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        Assert.Equal("127.0.0.1", builder.Host);
        Assert.Equal("sentinel_integration_test", builder.Database);
        Assert.Equal("sentinel_test", builder.Username);
    }

    [Fact]
    public void Constructor_Rejects_Docker_Host_Without_Explicit_OptIn()
    {
        using var connection = TestEnvironmentVariableScope.Set(
            PostgresIntegrationTestFixture.ConnectionEnvironmentVariable,
            "Host=postgres;Database=sentinel_integration_test;Username=integration;Password=test");
        using var dockerHostOptIn = TestEnvironmentVariableScope.Set("SENTINEL_POSTGRES_TEST_ALLOW_DOCKER_HOST", null);

        var exception = Assert.Throws<InvalidOperationException>(() => new PostgresIntegrationTestFixture());

        Assert.Contains("only allowed against localhost", exception.Message);
    }

    [Fact]
    public void Constructor_Allows_Isolated_Docker_Host_With_Explicit_OptIn()
    {
        using var connection = TestEnvironmentVariableScope.Set(
            PostgresIntegrationTestFixture.ConnectionEnvironmentVariable,
            "Host=postgres;Database=sentinel_integration_test;Username=integration;Password=test");
        using var dockerHostOptIn = TestEnvironmentVariableScope.Set("SENTINEL_POSTGRES_TEST_ALLOW_DOCKER_HOST", "true");

        var fixture = new PostgresIntegrationTestFixture();

        Assert.Equal("postgres", new NpgsqlConnectionStringBuilder(fixture.ConnectionString).Host);
    }

    [Fact]
    public void Constructor_Rejects_SentinelDev_Database()
    {
        using var env = TestEnvironmentVariableScope.Set(
            PostgresIntegrationTestFixture.ConnectionEnvironmentVariable,
            "Host=127.0.0.1;Port=5432;Database=sentinel_dev;Username=sentinel_test;Password=not-used");

        var ex = Assert.Throws<InvalidOperationException>(() => new PostgresIntegrationTestFixture());

        Assert.Contains("sentinel_integration_test", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_Rejects_Remote_Host()
    {
        using var env = TestEnvironmentVariableScope.Set(
            PostgresIntegrationTestFixture.ConnectionEnvironmentVariable,
            "Host=10.10.10.10;Port=5432;Database=sentinel_integration_test;Username=sentinel_test;Password=not-used");

        var ex = Assert.Throws<InvalidOperationException>(() => new PostgresIntegrationTestFixture());

        Assert.Contains("localhost", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_Rejects_Missing_Configuration()
    {
        using var env = TestEnvironmentVariableScope.Set(PostgresIntegrationTestFixture.ConnectionEnvironmentVariable, null);

        var ex = Assert.Throws<InvalidOperationException>(() => new PostgresIntegrationTestFixture());

        Assert.Contains(PostgresIntegrationTestFixture.ConnectionEnvironmentVariable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_Does_Not_Access_Database()
    {
        using var env = TestEnvironmentVariableScope.Set(
            PostgresIntegrationTestFixture.ConnectionEnvironmentVariable,
            "Host=localhost;Port=1;Database=sentinel_integration_test;Username=sentinel_test;Password=not-used");

        var fixture = new PostgresIntegrationTestFixture();

        Assert.NotNull(fixture);
    }

    private sealed class TestEnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        private TestEnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public static TestEnvironmentVariableScope Set(string name, string? value) => new(name, value);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
        }
    }
}
