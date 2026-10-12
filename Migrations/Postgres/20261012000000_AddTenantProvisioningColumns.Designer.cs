using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentinelMfgSuite.Web.Data;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261012000000_AddTenantProvisioningColumns")]
    partial class AddTenantProvisioningColumns
    {
        // Designer metadata to allow EF to discover the migration by id.
    }
}
