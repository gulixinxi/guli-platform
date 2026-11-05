using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReverseLookupRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reverse_lookup_records",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    InputWebsite = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false),
                    QueriedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reverse_lookup_records", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reverse_lookup_records_BatchId",
                schema: "crm",
                table: "reverse_lookup_records",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_reverse_lookup_records_TenantId_CompanyId_QueriedAt",
                schema: "crm",
                table: "reverse_lookup_records",
                columns: new[] { "TenantId", "CompanyId", "QueriedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reverse_lookup_records",
                schema: "crm");
        }
    }
}
