using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using SentinelMfgSuite.Web.Data;
using System;

namespace SentinelMfgSuite.Web.Data
{
    // Design-time factory to allow EF tools to create ApplicationDbContext without running Program.
    // Reads connection string from environment or standard configuration providers.
    public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext(string[] args)
        {
            // Build configuration from environment and optional local files so tooling can pick up a design-time connection
            var builder = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables();

            var configuration = builder.Build();

            // Try common sources for a design-time connection string. Environment variables take precedence.
            var conn = Environment.GetEnvironmentVariable("DESIGNTIME_CONNECTION");
            if (string.IsNullOrEmpty(conn))
                conn = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
            if (string.IsNullOrEmpty(conn))
                conn = configuration.GetConnectionString("DefaultConnection");

            if (string.IsNullOrEmpty(conn))
            {
                throw new InvalidOperationException("A design-time connection string must be provided for EF tooling. Set environment variable DESIGNTIME_CONNECTION or ConnectionStrings__DefaultConnection, or add appsettings.Development.json.");
            }

            var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
            optionsBuilder.UseNpgsql(conn);

            return new ApplicationDbContext(optionsBuilder.Options);
        }
    }
}
