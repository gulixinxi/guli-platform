using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQuakeAssetRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quake_asset_records",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Transport = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Port = table.Column<int>(type: "integer", nullable: true),
                    SitePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Asn = table.Column<long>(type: "bigint", nullable: true),
                    AsnOrganization = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    Hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    ServiceProduct = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ServiceName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ServiceVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CountryCn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CityCn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProvinceCn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ServerHeader = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VendorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SceneCn = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AsnName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OsName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    QuakeAssetId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CleanVerdict = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CleanScore = table.Column<int>(type: "integer", nullable: false),
                    CleanReasons = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Imported = table.Column<bool>(type: "boolean", nullable: false),
                    ImportedLeadId = table.Column<long>(type: "bigint", nullable: true),
                    RawJson = table.Column<string>(type: "jsonb", nullable: false),
                    ObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quake_asset_records", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_quake_asset_records_BatchId",
                schema: "crm",
                table: "quake_asset_records",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_quake_asset_records_ObservedAt",
                schema: "crm",
                table: "quake_asset_records",
                column: "ObservedAt");

            migrationBuilder.CreateIndex(
                name: "IX_quake_asset_records_TenantId_CompanyId_CleanVerdict",
                schema: "crm",
                table: "quake_asset_records",
                columns: new[] { "TenantId", "CompanyId", "CleanVerdict" });

            migrationBuilder.CreateIndex(
                name: "IX_quake_asset_records_TenantId_CompanyId_Domain_Port",
                schema: "crm",
                table: "quake_asset_records",
                columns: new[] { "TenantId", "CompanyId", "Domain", "Port" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quake_asset_records",
                schema: "crm");
        }
    }
}
