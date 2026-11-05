using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityCandidatesAndEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "identity_candidates",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    AssetId = table.Column<long>(type: "bigint", nullable: false),
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    ProposedName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<long>(type: "bigint", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedBy = table.Column<long>(type: "bigint", nullable: true),
                    ReviewReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_candidates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "identity_evidence",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    CandidateId = table.Column<long>(type: "bigint", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ClaimText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedBy = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_evidence", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "identity_review_events",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    CandidateId = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_review_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_identity_candidates_TenantId_CompanyId_AssetId_Status",
                schema: "crm",
                table: "identity_candidates",
                columns: new[] { "TenantId", "CompanyId", "AssetId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_identity_candidates_TenantId_CompanyId_Domain",
                schema: "crm",
                table: "identity_candidates",
                columns: new[] { "TenantId", "CompanyId", "Domain" });

            migrationBuilder.CreateIndex(
                name: "IX_identity_evidence_TenantId_CompanyId_CandidateId_RecordedAt",
                schema: "crm",
                table: "identity_evidence",
                columns: new[] { "TenantId", "CompanyId", "CandidateId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_identity_review_events_TenantId_CompanyId_CandidateId_Versi~",
                schema: "crm",
                table: "identity_review_events",
                columns: new[] { "TenantId", "CompanyId", "CandidateId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "identity_candidates",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "identity_evidence",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "identity_review_events",
                schema: "crm");
        }
    }
}
