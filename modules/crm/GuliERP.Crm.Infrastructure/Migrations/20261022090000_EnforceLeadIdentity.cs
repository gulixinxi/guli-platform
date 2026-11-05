using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261022090000_EnforceLeadIdentity")]
public partial class EnforceLeadIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Never merge or discard existing records in a schema migration. An operator
        // must review conflicting identities before this constraint can be applied.
        migrationBuilder.Sql("""
            DO $$ BEGIN
              IF EXISTS (
                SELECT 1 FROM crm.leads
                WHERE NULLIF(btrim("CreditCode"), '') IS NOT NULL
                GROUP BY "TenantId", "CompanyId", upper(btrim("CreditCode"))
                HAVING count(*) > 1
              ) THEN
                RAISE EXCEPTION 'Duplicate CRM lead credit codes require review before EnforceLeadIdentity';
              END IF;
              IF EXISTS (
                SELECT 1 FROM crm.leads
                GROUP BY "TenantId", "CompanyId", lower(btrim("CompanyName"))
                HAVING count(*) > 1
              ) THEN
                RAISE EXCEPTION 'Duplicate CRM lead names require review before EnforceLeadIdentity';
              END IF;
            END $$;
            """);
        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX "UX_leads_scope_credit_normalized"
            ON crm.leads ("TenantId", "CompanyId", upper(btrim("CreditCode")))
            WHERE NULLIF(btrim("CreditCode"), '') IS NOT NULL;
            """);
        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX "UX_leads_scope_name_normalized"
            ON crm.leads ("TenantId", "CompanyId", lower(btrim("CompanyName")));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX crm.\"UX_leads_scope_name_normalized\";");
        migrationBuilder.Sql("DROP INDEX crm.\"UX_leads_scope_credit_normalized\";");
    }
}
