using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadDemandAssessmentRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lead_demand_assessment_revisions",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    Category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Confidence = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Evidence = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NextAction = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReviewerId = table.Column<long>(type: "bigint", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lead_demand_assessment_revisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lead_demand_assessment_revisions_TenantId_CompanyId_LeadId_~",
                schema: "crm",
                table: "lead_demand_assessment_revisions",
                columns: new[] { "TenantId", "CompanyId", "LeadId", "Category", "Version" },
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO crm.lead_demand_assessment_revisions
                    ("TenantId", "CompanyId", "LeadId", "Category", "State", "Confidence", "Evidence",
                     "Reason", "NextAction", "ReviewerId", "ReviewedAt", "Version")
                SELECT "TenantId", "CompanyId", "LeadId", "Category", "State", "Confidence", "Evidence",
                       "Reason", "NextAction", "ReviewerId", "ReviewedAt", "Version"
                FROM crm.lead_demand_assessments;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_demand_assessment_revisions",
                schema: "crm");
        }
    }
}
