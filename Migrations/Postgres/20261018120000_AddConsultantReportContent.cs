using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentinelMfgSuite.Web.Data;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261018120000_AddConsultantReportContent")]
    public partial class AddConsultantReportContent : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConsultantContentJson",
                table: "AssessmentReportRevisions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsultantContentSchemaVersion",
                table: "AssessmentReportRevisions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsultantContentJson",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropColumn(
                name: "ConsultantContentSchemaVersion",
                table: "AssessmentReportRevisions");
        }
    }
}