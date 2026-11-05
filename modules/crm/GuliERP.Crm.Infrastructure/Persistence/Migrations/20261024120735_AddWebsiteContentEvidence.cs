using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebsiteContentEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "website_content_evidence",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    CandidateId = table.Column<long>(type: "bigint", nullable: false),
                    ReportId = table.Column<long>(type: "bigint", nullable: false),
                    ScenarioVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MethodVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Excerpt = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Observation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Limitation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedBy = table.Column<long>(type: "bigint", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedBy = table.Column<long>(type: "bigint", nullable: true),
                    ReviewReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_website_content_evidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_website_content_evidence_identity_candidates_TenantId_Compa~",
                        columns: x => new { x.TenantId, x.CompanyId, x.CandidateId },
                        principalSchema: "crm",
                        principalTable: "identity_candidates",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_website_content_evidence_website_diagnostic_reports_ReportId",
                        column: x => x.ReportId,
                        principalSchema: "crm",
                        principalTable: "website_diagnostic_reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_website_content_evidence_ReportId",
                schema: "crm",
                table: "website_content_evidence",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_website_content_evidence_TenantId_CompanyId_CandidateId_Rec~",
                schema: "crm",
                table: "website_content_evidence",
                columns: new[] { "TenantId", "CompanyId", "CandidateId", "RecordedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "website_content_evidence",
                schema: "crm");
        }
    }
}
