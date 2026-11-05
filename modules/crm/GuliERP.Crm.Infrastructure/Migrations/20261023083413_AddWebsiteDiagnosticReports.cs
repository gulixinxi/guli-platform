using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebsiteDiagnosticReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "website_diagnostic_reports",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    CandidateId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MethodVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChecksJson = table.Column<string>(type: "jsonb", nullable: false),
                    AttemptLogJson = table.Column<string>(type: "jsonb", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequestedBy = table.Column<long>(type: "bigint", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_website_diagnostic_reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_website_diagnostic_reports_identity_candidates_TenantId_Com~",
                        columns: x => new { x.TenantId, x.CompanyId, x.CandidateId },
                        principalSchema: "crm",
                        principalTable: "identity_candidates",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_website_diagnostic_reports_Status_NextAttemptAt_RequestedAt",
                schema: "crm",
                table: "website_diagnostic_reports",
                columns: new[] { "Status", "NextAttemptAt", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_website_diagnostic_reports_TenantId_CompanyId_CandidateId_V~",
                schema: "crm",
                table: "website_diagnostic_reports",
                columns: new[] { "TenantId", "CompanyId", "CandidateId", "Version" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION crm.prevent_completed_website_report_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."Status" IN ('completed', 'failed') THEN
                        RAISE EXCEPTION 'Completed website diagnostic reports are immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER website_report_completed_immutable
                BEFORE UPDATE OR DELETE ON crm.website_diagnostic_reports
                FOR EACH ROW EXECUTE FUNCTION crm.prevent_completed_website_report_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER website_report_completed_immutable ON crm.website_diagnostic_reports;
                DROP FUNCTION crm.prevent_completed_website_report_mutation();
                """);
            migrationBuilder.DropTable(
                name: "website_diagnostic_reports",
                schema: "crm");
        }
    }
}
