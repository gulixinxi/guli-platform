using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLegacyRequestObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "legacy_request_observations",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    SourceCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Route = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legacy_request_observations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_legacy_request_observations_TenantId_CompanyId_SourceCode_O~",
                schema: "crm",
                table: "legacy_request_observations",
                columns: new[] { "TenantId", "CompanyId", "SourceCode", "ObservedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legacy_request_observations",
                schema: "crm");
        }
    }
}
