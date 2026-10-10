using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentinelMfgSuite.Web.Data;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260101000000_InitialPostgres")]
    partial class InitialPostgres
    {
        // Designer metadata file to enable EF migration discovery for provider-specific migration.
        // BuildTargetModel is provided by the canonical snapshot (ApplicationDbContextModelSnapshot_Postgres.cs)
    }
}
