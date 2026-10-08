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
            var builder = new ConfigurationBuilder()
                .AddEnvironmentVariables();

            var configuration = builder.Build();

            var conn = configuration["ConnectionStrings__DefaultConnection"];
            if (string.IsNullOrEmpty(conn))
            {
                throw new InvalidOperationException("ConnectionStrings__DefaultConnection environment variable must be set for design-time EF tooling.");
            }

            var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
            optionsBuilder.UseNpgsql(conn);

            return new ApplicationDbContext(optionsBuilder.Options);
        }
    }
}
