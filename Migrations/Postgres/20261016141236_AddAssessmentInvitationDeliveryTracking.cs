using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddAssessmentInvitationDeliveryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveryAcceptedUtc",
                table: "AssessmentInvitation",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeliveryAttemptCount",
                table: "AssessmentInvitation",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryFailureCode",
                table: "AssessmentInvitation",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveryLastAttemptUtc",
                table: "AssessmentInvitation",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "DeliveryStatus",
                table: "AssessmentInvitation",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryAcceptedUtc",
                table: "AssessmentInvitation");

            migrationBuilder.DropColumn(
                name: "DeliveryAttemptCount",
                table: "AssessmentInvitation");

            migrationBuilder.DropColumn(
                name: "DeliveryFailureCode",
                table: "AssessmentInvitation");

            migrationBuilder.DropColumn(
                name: "DeliveryLastAttemptUtc",
                table: "AssessmentInvitation");

            migrationBuilder.DropColumn(
                name: "DeliveryStatus",
                table: "AssessmentInvitation");
        }
    }
}
