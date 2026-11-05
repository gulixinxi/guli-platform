using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebsiteRecheckChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "website_recheck_changes",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    CandidateId = table.Column<long>(type: "bigint", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IntervalDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 7),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedBy = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_website_recheck_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_website_recheck_changes_identity_candidates_TenantId_Compan~",
                        columns: x => new { x.TenantId, x.CompanyId, x.CandidateId },
                        principalSchema: "crm",
                        principalTable: "identity_candidates",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_website_recheck_changes_CandidateId_ChangedAt",
                schema: "crm",
                table: "website_recheck_changes",
                columns: new[] { "CandidateId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_website_recheck_changes_TenantId_CompanyId_CandidateId",
                schema: "crm",
                table: "website_recheck_changes",
                columns: new[] { "TenantId", "CompanyId", "CandidateId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "website_recheck_changes",
                schema: "crm");
        }
    }
}
