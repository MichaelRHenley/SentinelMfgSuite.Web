using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddAssessmentQuestionnaireVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QuestionnaireVersion",
                table: "Assessment",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "1.0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuestionnaireVersion",
                table: "Assessment");
        }
    }
}
