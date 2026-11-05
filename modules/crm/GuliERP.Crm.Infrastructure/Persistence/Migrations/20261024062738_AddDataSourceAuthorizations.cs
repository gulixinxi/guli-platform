using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSourceAuthorizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_source_authorizations",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    SourceCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AuthorizationReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PermittedFields = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DailyQuota = table.Column<int>(type: "integer", nullable: true),
                    MonthlyQuota = table.Column<int>(type: "integer", nullable: true),
                    CostNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_source_authorizations", x => x.Id);
                    table.UniqueConstraint("AK_data_source_authorizations_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "data_source_authorization_revisions",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    AuthorizationId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AuthorizationReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PermittedFields = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DailyQuota = table.Column<int>(type: "integer", nullable: true),
                    MonthlyQuota = table.Column<int>(type: "integer", nullable: true),
                    CostNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActorId = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_source_authorization_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_data_source_authorization_revisions_data_source_authorizati~",
                        columns: x => new { x.TenantId, x.CompanyId, x.AuthorizationId },
                        principalSchema: "crm",
                        principalTable: "data_source_authorizations",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_data_source_authorization_revisions_TenantId_CompanyId_Auth~",
                schema: "crm",
                table: "data_source_authorization_revisions",
                columns: new[] { "TenantId", "CompanyId", "AuthorizationId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_data_source_authorizations_TenantId_CompanyId_SourceCode",
                schema: "crm",
                table: "data_source_authorizations",
                columns: new[] { "TenantId", "CompanyId", "SourceCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_source_authorization_revisions",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "data_source_authorizations",
                schema: "crm");
        }
    }
}
