using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations
{
    /// <inheritdoc />
    public partial class Baseline_ExistingDb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // baseline only: database schema already exists
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // baseline only
        }
    }
}
