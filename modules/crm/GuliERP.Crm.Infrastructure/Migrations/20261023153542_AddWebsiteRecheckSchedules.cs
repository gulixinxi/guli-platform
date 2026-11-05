using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebsiteRecheckSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Trigger",
                schema: "crm",
                table: "website_diagnostic_reports",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.CreateTable(
                name: "website_recheck_schedules",
                schema: "crm",
                columns: table => new
                {
                    CandidateId = table.Column<long>(type: "bigint", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IntervalDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 7),
                    NextDueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastQueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_website_recheck_schedules", x => x.CandidateId);
                    table.ForeignKey(
                        name: "FK_website_recheck_schedules_identity_candidates_TenantId_Comp~",
                        columns: x => new { x.TenantId, x.CompanyId, x.CandidateId },
                        principalSchema: "crm",
                        principalTable: "identity_candidates",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_website_recheck_schedules_Enabled_NextDueAt",
                schema: "crm",
                table: "website_recheck_schedules",
                columns: new[] { "Enabled", "NextDueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_website_recheck_schedules_TenantId_CompanyId_CandidateId",
                schema: "crm",
                table: "website_recheck_schedules",
                columns: new[] { "TenantId", "CompanyId", "CandidateId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "website_recheck_schedules",
                schema: "crm");

            migrationBuilder.DropColumn(
                name: "Trigger",
                schema: "crm",
                table: "website_diagnostic_reports");
        }
    }
}
