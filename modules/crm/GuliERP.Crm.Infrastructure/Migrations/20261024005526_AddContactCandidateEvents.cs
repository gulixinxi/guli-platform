using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContactCandidateEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_contact_candidates_TenantId_CompanyId_Id",
                schema: "crm",
                table: "contact_candidates",
                columns: new[] { "TenantId", "CompanyId", "Id" });

            migrationBuilder.CreateTable(
                name: "contact_candidate_events",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    CandidateId = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ActorId = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_candidate_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_contact_candidate_events_contact_candidates_TenantId_Compan~",
                        columns: x => new { x.TenantId, x.CompanyId, x.CandidateId },
                        principalSchema: "crm",
                        principalTable: "contact_candidates",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contact_candidate_events_CandidateId_OccurredAt",
                schema: "crm",
                table: "contact_candidate_events",
                columns: new[] { "CandidateId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_contact_candidate_events_TenantId_CompanyId_CandidateId",
                schema: "crm",
                table: "contact_candidate_events",
                columns: new[] { "TenantId", "CompanyId", "CandidateId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contact_candidate_events",
                schema: "crm");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_contact_candidates_TenantId_CompanyId_Id",
                schema: "crm",
                table: "contact_candidates");
        }
    }
}
