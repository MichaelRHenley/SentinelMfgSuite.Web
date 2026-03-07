using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations
{
    /// <inheritdoc />
    public partial class FixTenantSubscriptionStripeNullability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.Sql(@"
IF COL_LENGTH('dbo.AspNetUsers', 'DisplayName') IS NULL
BEGIN
    ALTER TABLE dbo.AspNetUsers ADD DisplayName nvarchar(max) NULL;
END;

IF COL_LENGTH('dbo.AspNetUsers', 'CompanyName') IS NULL
BEGIN
    ALTER TABLE dbo.AspNetUsers ADD CompanyName nvarchar(max) NULL;
END;
");

            // 1) Ensure Identity user columns exist
            migrationBuilder.Sql(@"
IF COL_LENGTH('dbo.AspNetUsers', 'ProvisioningState') IS NULL
BEGIN
    ALTER TABLE dbo.AspNetUsers ADD ProvisioningState int NOT NULL;
END;

-- Ensure a default exists for ProvisioningState
IF NOT EXISTS (
    SELECT 1
    FROM sys.default_constraints dc
    JOIN sys.columns c
      ON c.object_id = dc.parent_object_id
     AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID('dbo.AspNetUsers')
      AND c.name = 'ProvisioningState'
)
BEGIN
    ALTER TABLE dbo.AspNetUsers
        ADD CONSTRAINT DF_AspNetUsers_ProvisioningState DEFAULT(0) FOR ProvisioningState;
END;
");

            migrationBuilder.Sql(@"
IF COL_LENGTH('dbo.AspNetUsers', 'TenantId') IS NULL
BEGIN
    ALTER TABLE dbo.AspNetUsers ADD TenantId uniqueidentifier NULL;
END;
");



            // 2) Ensure Tenants table exists
            migrationBuilder.Sql(@"
IF OBJECT_ID('dbo.Tenants', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Tenants
    (
        Id uniqueidentifier NOT NULL,
        Name nvarchar(200) NOT NULL,
        Status int NOT NULL,
        CreatedUtc datetime2 NOT NULL,
        CONSTRAINT PK_Tenants PRIMARY KEY (Id)
    );
END;
");

            // 3) Ensure TenantUserRoles table exists
            migrationBuilder.Sql(@"
IF OBJECT_ID('dbo.TenantUserRoles', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantUserRoles
    (
        TenantId uniqueidentifier NOT NULL,
        UserId nvarchar(450) NOT NULL,
        Role int NOT NULL,
        CONSTRAINT PK_TenantUserRoles PRIMARY KEY (TenantId, UserId, Role)
    );
END;
");

            // 4) Ensure TenantSubscriptions table exists
            migrationBuilder.Sql(@"
IF OBJECT_ID('dbo.TenantSubscriptions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantSubscriptions
    (
        Id uniqueidentifier NOT NULL,
        TenantId uniqueidentifier NOT NULL,
        Tier nvarchar(50) NOT NULL,
        BillingCadence nvarchar(50) NOT NULL,
        StripeCustomerId nvarchar(200) NULL,
        StripeSubscriptionId nvarchar(200) NULL,
        IsActive bit NOT NULL,
        StartedUtc datetime2 NOT NULL,
        CONSTRAINT PK_TenantSubscriptions PRIMARY KEY (Id),
        CONSTRAINT FK_TenantSubscriptions_Tenants_TenantId
            FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE CASCADE
    );
END;
");

            migrationBuilder.Sql(@"
IF OBJECT_ID('dbo.TenantSubscriptions', 'U') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = 'FK_TenantSubscriptions_Tenants_TenantId'
      AND parent_object_id = OBJECT_ID('dbo.TenantSubscriptions')
)
BEGIN
    ALTER TABLE dbo.TenantSubscriptions WITH CHECK
    ADD CONSTRAINT FK_TenantSubscriptions_Tenants_TenantId
        FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE CASCADE;
END;
");


            // 5) Enforce Stripe fields as nullable (even if table already existed)
            migrationBuilder.Sql(@"
IF OBJECT_ID('dbo.TenantSubscriptions', 'U') IS NOT NULL
BEGIN
    ALTER TABLE dbo.TenantSubscriptions ALTER COLUMN StripeCustomerId nvarchar(200) NULL;
    ALTER TABLE dbo.TenantSubscriptions ALTER COLUMN StripeSubscriptionId nvarchar(200) NULL;
END;
");

            // 6) Create the unique index if missing
            migrationBuilder.Sql(@"
IF OBJECT_ID('dbo.TenantSubscriptions', 'U') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_TenantSubscriptions_TenantId'
      AND object_id = OBJECT_ID('dbo.TenantSubscriptions')
)
BEGIN
    CREATE UNIQUE INDEX IX_TenantSubscriptions_TenantId
        ON dbo.TenantSubscriptions(TenantId);
END;
");
        }


        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Baseline/repair migration. Intentionally no down.
        }

    }
}
