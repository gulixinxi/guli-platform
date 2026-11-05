using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProtectIdentityAuditRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION crm.prevent_identity_audit_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Identity evidence and review events are append-only';
                END;
                $$;
                CREATE TRIGGER identity_evidence_append_only
                BEFORE UPDATE OR DELETE ON crm.identity_evidence
                FOR EACH ROW EXECUTE FUNCTION crm.prevent_identity_audit_mutation();
                CREATE TRIGGER identity_review_events_append_only
                BEFORE UPDATE OR DELETE ON crm.identity_review_events
                FOR EACH ROW EXECUTE FUNCTION crm.prevent_identity_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER identity_review_events_append_only ON crm.identity_review_events;
                DROP TRIGGER identity_evidence_append_only ON crm.identity_evidence;
                DROP FUNCTION crm.prevent_identity_audit_mutation();
                """);
        }
    }
}
