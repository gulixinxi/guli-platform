using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadAssessmentEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lead_assessment_evidence",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    AssessmentId = table.Column<long>(type: "bigint", nullable: false),
                    Category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    EvidenceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VerificationStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lead_assessment_evidence", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lead_assessment_evidence_TenantId_CompanyId_AssessmentId_Ve~",
                schema: "crm",
                table: "lead_assessment_evidence",
                columns: new[] { "TenantId", "CompanyId", "AssessmentId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_assessment_evidence_TenantId_CompanyId_LeadId_Category",
                schema: "crm",
                table: "lead_assessment_evidence",
                columns: new[] { "TenantId", "CompanyId", "LeadId", "Category" });

            // ----------------------------------------------------------------
            // Data backfill: structured-ise the pre-existing free-text evidence.
            //
            // Every assessment that carries a non-blank `evidence` string gets
            // exactly one row of type 'legacy_text'. We COPY rather than move:
            // the old column is left intact so the pre-migration state stays
            // inspectable, which is also what makes this backfill verifiable
            // (row count of the new table must equal the count of non-blank
            // evidence strings).
            //
            // collected_at is deliberately NULL, not reviewed_at: we know when
            // the reviewer typed the text, not when the underlying fact was
            // observed. Filling it with reviewed_at would manufacture a
            // provenance the data does not support.
            //
            // verification_status is 'not_verified' — nobody verified these.
            // ----------------------------------------------------------------
            // NOTE on identifiers: tables here are snake_case (ToTable) but
            // the COLUMNS are PascalCase — EF emits them quoted, and this
            // database has no snake_case column convention. An unquoted
            // snake_case column does not exist and fails with 42703. Every
            // identifier in this statement is quoted accordingly.
            migrationBuilder.Sql(@"
INSERT INTO crm.""lead_assessment_evidence""
    (""TenantId"", ""CompanyId"", ""LeadId"", ""AssessmentId"", ""Category"", ""Version"",
     ""EvidenceType"", ""Url"", ""CollectedAt"", ""VerificationStatus"", ""Note"")
SELECT a.""TenantId"",
       a.""CompanyId"",
       a.""LeadId"",
       a.""Id"",
       a.""Category"",
       a.""Version"",
       'legacy_text',
       NULL,
       NULL,
       'not_verified',
       a.""Evidence""
FROM crm.""lead_demand_assessments"" a
WHERE a.""Evidence"" IS NOT NULL
  AND btrim(a.""Evidence"") <> ''
  AND NOT EXISTS (
      SELECT 1 FROM crm.""lead_assessment_evidence"" e
      WHERE e.""AssessmentId"" = a.""Id""
  );");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_assessment_evidence",
                schema: "crm");
        }
    }
}
