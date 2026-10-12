using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    public partial class AddTenantProvisioningColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Use idempotent SQL so this migration is safe to run against production
            // where the columns may already have been added manually.
            migrationBuilder.Sql("DO $$\nBEGIN\n    -- Add TenantId column if it does not exist\n    IF NOT EXISTS (\n        SELECT 1 FROM information_schema.columns\n        WHERE table_schema = 'public' AND table_name = 'AspNetUsers' AND column_name = 'TenantId') THEN\n        ALTER TABLE public.\"AspNetUsers\" ADD COLUMN \"TenantId\" uuid NULL;\n    END IF;\n\n    -- Add ProvisioningState column if it does not exist (integer with default 0)\n    IF NOT EXISTS (\n        SELECT 1 FROM information_schema.columns\n        WHERE table_schema = 'public' AND table_name = 'AspNetUsers' AND column_name = 'ProvisioningState') THEN\n        ALTER TABLE public.\"AspNetUsers\" ADD COLUMN \"ProvisioningState\" integer DEFAULT 0;\n    END IF;\n\n    -- Ensure ProvisioningState has no NULLs and enforce NOT NULL constraint\n    -- Set any existing NULLs to 0 (Registered) then make column NOT NULL and keep DEFAULT 0\n    IF EXISTS (\n        SELECT 1 FROM information_schema.columns\n        WHERE table_schema = 'public' AND table_name = 'AspNetUsers' AND column_name = 'ProvisioningState') THEN\n        UPDATE public.\"AspNetUsers\" SET \"ProvisioningState\" = 0 WHERE \"ProvisioningState\" IS NULL;\n        ALTER TABLE public.\"AspNetUsers\" ALTER COLUMN \"ProvisioningState\" SET DEFAULT 0;\n        ALTER TABLE public.\"AspNetUsers\" ALTER COLUMN \"ProvisioningState\" SET NOT NULL;\n    END IF;\nEND$$;" );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Use IF EXISTS / IF NOT EXISTS to safely undo without throwing when columns are missing
            migrationBuilder.Sql("DO $$\nBEGIN\n    IF EXISTS (\n        SELECT 1 FROM information_schema.columns\n        WHERE table_schema = 'public' AND table_name = 'AspNetUsers' AND column_name = 'ProvisioningState') THEN\n        ALTER TABLE public.\"AspNetUsers\" DROP COLUMN IF EXISTS \"ProvisioningState\";\n    END IF;\n\n    IF EXISTS (\n        SELECT 1 FROM information_schema.columns\n        WHERE table_schema = 'public' AND table_name = 'AspNetUsers' AND column_name = 'TenantId') THEN\n        ALTER TABLE public.\"AspNetUsers\" DROP COLUMN IF EXISTS \"TenantId\";\n    END IF;\nEND$$;" );
        }
    }
}
