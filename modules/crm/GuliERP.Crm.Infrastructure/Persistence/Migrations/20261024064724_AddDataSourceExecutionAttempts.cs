using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSourceExecutionAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_source_execution_attempts",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    AuthorizationId = table.Column<long>(type: "bigint", nullable: false),
                    SourceCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    RequestedFields = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ReservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_source_execution_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_data_source_execution_attempts_data_source_authorizations_T~",
                        columns: x => new { x.TenantId, x.CompanyId, x.AuthorizationId },
                        principalSchema: "crm",
                        principalTable: "data_source_authorizations",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_data_source_execution_attempts_TenantId_CompanyId_Authoriza~",
                schema: "crm",
                table: "data_source_execution_attempts",
                columns: new[] { "TenantId", "CompanyId", "AuthorizationId", "ReservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_data_source_execution_attempts_TenantId_CompanyId_RequestId~",
                schema: "crm",
                table: "data_source_execution_attempts",
                columns: new[] { "TenantId", "CompanyId", "RequestId", "AttemptNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_source_execution_attempts",
                schema: "crm");
        }
    }
}
