using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LinkPromotedIdentityLead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SourceIdentityCandidateId",
                schema: "crm",
                table: "leads",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_leads_TenantId_CompanyId_SourceIdentityCandidateId",
                schema: "crm",
                table: "leads",
                columns: new[] { "TenantId", "CompanyId", "SourceIdentityCandidateId" },
                unique: true,
                filter: "\"SourceIdentityCandidateId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_leads_identity_candidates_TenantId_CompanyId_SourceIdentity~",
                schema: "crm",
                table: "leads",
                columns: new[] { "TenantId", "CompanyId", "SourceIdentityCandidateId" },
                principalSchema: "crm",
                principalTable: "identity_candidates",
                principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_leads_identity_candidates_TenantId_CompanyId_SourceIdentity~",
                schema: "crm",
                table: "leads");

            migrationBuilder.DropIndex(
                name: "IX_leads_TenantId_CompanyId_SourceIdentityCandidateId",
                schema: "crm",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "SourceIdentityCandidateId",
                schema: "crm",
                table: "leads");
        }
    }
}
