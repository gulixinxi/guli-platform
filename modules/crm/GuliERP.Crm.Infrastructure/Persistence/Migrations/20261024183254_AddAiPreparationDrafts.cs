using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiPreparationDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_preparation_drafts",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    CandidateId = table.Column<long>(type: "bigint", nullable: false),
                    RequestKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    TaskType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProviderCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ContractVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InputEvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    ClaimsJson = table.Column<string>(type: "jsonb", nullable: false),
                    EstimatedCostCents = table.Column<int>(type: "integer", nullable: false),
                    MaxCostCents = table.Column<int>(type: "integer", nullable: false),
                    ActualCostCents = table.Column<int>(type: "integer", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<long>(type: "bigint", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedBy = table.Column<long>(type: "bigint", nullable: true),
                    ReviewReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_preparation_drafts", x => x.Id);
                    table.CheckConstraint("CK_ai_preparation_cost", "\"Version\" > 0 AND \"EstimatedCostCents\" >= 0 AND \"MaxCostCents\" >= 0 AND \"ActualCostCents\" >= 0");
                    table.CheckConstraint("CK_ai_preparation_status", "\"Status\" IN ('draft','unsupported','failed','cost_limit','approved','rejected')");
                    table.ForeignKey(
                        name: "FK_ai_preparation_drafts_identity_candidates_TenantId_CompanyI~",
                        columns: x => new { x.TenantId, x.CompanyId, x.CandidateId },
                        principalSchema: "crm",
                        principalTable: "identity_candidates",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_preparation_drafts_TenantId_CompanyId_CandidateId_TaskTy~",
                schema: "crm",
                table: "ai_preparation_drafts",
                columns: new[] { "TenantId", "CompanyId", "CandidateId", "TaskType", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_preparation_drafts_TenantId_CompanyId_RequestKey",
                schema: "crm",
                table: "ai_preparation_drafts",
                columns: new[] { "TenantId", "CompanyId", "RequestKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_preparation_drafts",
                schema: "crm");
        }
    }
}
