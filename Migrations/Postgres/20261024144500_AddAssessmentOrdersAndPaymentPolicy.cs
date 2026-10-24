using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddAssessmentOrdersAndPaymentPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "PaymentPolicy",
                table: "Assessment",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.CreateTable(
                name: "AssessmentOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerOrganization = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BillingContactName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BillingContactEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    TaxTreatment = table.Column<short>(type: "smallint", nullable: false),
                    TaxRate = table.Column<decimal>(type: "numeric(7,5)", nullable: false),
                    TaxLabel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TaxAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    PaymentStatus = table.Column<short>(type: "smallint", nullable: false),
                    PaymentMethod = table.Column<short>(type: "smallint", nullable: false),
                    PaymentProvider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ProviderPaymentReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PaymentReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VerifiedPaymentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerifiedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PolicyReviewRequired = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssessmentOrders_Assessment_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "Assessment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssessmentPaymentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<short>(type: "smallint", nullable: false),
                    FromStatus = table.Column<short>(type: "smallint", nullable: true),
                    ToStatus = table.Column<short>(type: "smallint", nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorType = table.Column<short>(type: "smallint", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ProviderEventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PaymentReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Metadata = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentPaymentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssessmentPaymentEvents_AssessmentOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "AssessmentOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentOrders_AssessmentId",
                table: "AssessmentOrders",
                column: "AssessmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentPaymentEvents_OrderId",
                table: "AssessmentPaymentEvents",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentPaymentEvents_Provider_ProviderEventId",
                table: "AssessmentPaymentEvents",
                columns: new[] { "Provider", "ProviderEventId" },
                unique: true,
                filter: "\"ProviderEventId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssessmentPaymentEvents");

            migrationBuilder.DropTable(
                name: "AssessmentOrders");

            migrationBuilder.DropColumn(
                name: "PaymentPolicy",
                table: "Assessment");
        }
    }
}
