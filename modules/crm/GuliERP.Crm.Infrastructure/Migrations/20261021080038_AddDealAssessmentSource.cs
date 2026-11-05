using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDealAssessmentSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SourceLeadDemandAssessmentId",
                schema: "crm",
                table: "deals",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceLeadDemandAssessmentVersion",
                schema: "crm",
                table: "deals",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_deals_SourceLeadDemandAssessmentId",
                schema: "crm",
                table: "deals",
                column: "SourceLeadDemandAssessmentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_deals_SourceLeadDemandAssessmentId",
                schema: "crm",
                table: "deals");

            migrationBuilder.DropColumn(
                name: "SourceLeadDemandAssessmentId",
                schema: "crm",
                table: "deals");

            migrationBuilder.DropColumn(
                name: "SourceLeadDemandAssessmentVersion",
                schema: "crm",
                table: "deals");
        }
    }
}
