using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceIdentityCandidateReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_quake_asset_records_TenantId_CompanyId_Id",
                schema: "crm",
                table: "quake_asset_records",
                columns: new[] { "TenantId", "CompanyId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_identity_candidates_TenantId_CompanyId_Id",
                schema: "crm",
                table: "identity_candidates",
                columns: new[] { "TenantId", "CompanyId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_identity_candidates_quake_asset_records_TenantId_CompanyId_~",
                schema: "crm",
                table: "identity_candidates",
                columns: new[] { "TenantId", "CompanyId", "AssetId" },
                principalSchema: "crm",
                principalTable: "quake_asset_records",
                principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_identity_evidence_identity_candidates_TenantId_CompanyId_Ca~",
                schema: "crm",
                table: "identity_evidence",
                columns: new[] { "TenantId", "CompanyId", "CandidateId" },
                principalSchema: "crm",
                principalTable: "identity_candidates",
                principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_identity_review_events_identity_candidates_TenantId_Company~",
                schema: "crm",
                table: "identity_review_events",
                columns: new[] { "TenantId", "CompanyId", "CandidateId" },
                principalSchema: "crm",
                principalTable: "identity_candidates",
                principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_identity_candidates_quake_asset_records_TenantId_CompanyId_~",
                schema: "crm",
                table: "identity_candidates");

            migrationBuilder.DropForeignKey(
                name: "FK_identity_evidence_identity_candidates_TenantId_CompanyId_Ca~",
                schema: "crm",
                table: "identity_evidence");

            migrationBuilder.DropForeignKey(
                name: "FK_identity_review_events_identity_candidates_TenantId_Company~",
                schema: "crm",
                table: "identity_review_events");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_quake_asset_records_TenantId_CompanyId_Id",
                schema: "crm",
                table: "quake_asset_records");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_identity_candidates_TenantId_CompanyId_Id",
                schema: "crm",
                table: "identity_candidates");
        }
    }
}
