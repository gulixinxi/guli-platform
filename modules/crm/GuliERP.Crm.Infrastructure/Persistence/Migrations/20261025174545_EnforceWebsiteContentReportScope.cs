using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceWebsiteContentReportScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail before changing constraints if legacy evidence points to a
            // report owned by another tenant, company, or identity candidate.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM crm.website_content_evidence AS evidence
                        LEFT JOIN crm.website_diagnostic_reports AS report
                          ON report."Id" = evidence."ReportId"
                         AND report."TenantId" = evidence."TenantId"
                         AND report."CompanyId" = evidence."CompanyId"
                         AND report."CandidateId" = evidence."CandidateId"
                        WHERE report."Id" IS NULL
                    ) THEN
                        RAISE EXCEPTION 'Website content evidence report scope mismatch requires manual review';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_website_content_evidence_website_diagnostic_reports_ReportId",
                schema: "crm",
                table: "website_content_evidence");

            migrationBuilder.DropIndex(
                name: "IX_website_content_evidence_ReportId",
                schema: "crm",
                table: "website_content_evidence");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_website_diagnostic_reports_TenantId_CompanyId_CandidateId_Id",
                schema: "crm",
                table: "website_diagnostic_reports",
                columns: new[] { "TenantId", "CompanyId", "CandidateId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_website_content_evidence_TenantId_CompanyId_CandidateId_Rep~",
                schema: "crm",
                table: "website_content_evidence",
                columns: new[] { "TenantId", "CompanyId", "CandidateId", "ReportId" });

            migrationBuilder.AddForeignKey(
                name: "FK_website_content_evidence_website_diagnostic_reports_TenantI~",
                schema: "crm",
                table: "website_content_evidence",
                columns: new[] { "TenantId", "CompanyId", "CandidateId", "ReportId" },
                principalSchema: "crm",
                principalTable: "website_diagnostic_reports",
                principalColumns: new[] { "TenantId", "CompanyId", "CandidateId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_website_content_evidence_website_diagnostic_reports_TenantI~",
                schema: "crm",
                table: "website_content_evidence");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_website_diagnostic_reports_TenantId_CompanyId_CandidateId_Id",
                schema: "crm",
                table: "website_diagnostic_reports");

            migrationBuilder.DropIndex(
                name: "IX_website_content_evidence_TenantId_CompanyId_CandidateId_Rep~",
                schema: "crm",
                table: "website_content_evidence");

            migrationBuilder.CreateIndex(
                name: "IX_website_content_evidence_ReportId",
                schema: "crm",
                table: "website_content_evidence",
                column: "ReportId");

            migrationBuilder.AddForeignKey(
                name: "FK_website_content_evidence_website_diagnostic_reports_ReportId",
                schema: "crm",
                table: "website_content_evidence",
                column: "ReportId",
                principalSchema: "crm",
                principalTable: "website_diagnostic_reports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
