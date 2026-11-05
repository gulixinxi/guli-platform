using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalCrmNumberSequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "company_no_seq",
                schema: "crm");

            migrationBuilder.CreateSequence(
                name: "deal_no_seq",
                schema: "crm");

            // Seed from every historical date, not only rows visible to one tenant.
            // Deployment must stop CRM writes while this migration and the new app version cut over.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    company_max numeric;
                    deal_max numeric;
                BEGIN
                    SELECT COALESCE(MAX(substring("CompanyNo" from 11)::numeric), 0)
                      INTO company_max
                      FROM crm.companies
                     WHERE "CompanyNo" ~ '^CO[0-9]{8}[0-9]{4,}$';
                    SELECT COALESCE(MAX(substring("DealNo" from 11)::numeric), 0)
                      INTO deal_max
                      FROM crm.deals
                     WHERE "DealNo" ~ '^DE[0-9]{8}[0-9]{4,}$';

                    IF company_max >= 9223372036854775807 OR deal_max >= 9223372036854775807 THEN
                        RAISE EXCEPTION 'CRM display number suffix exceeds sequence capacity';
                    END IF;

                    PERFORM setval('crm.company_no_seq'::regclass, (company_max + 1)::bigint, false);
                    PERFORM setval('crm.deal_no_seq'::regclass, (deal_max + 1)::bigint, false);
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "CRM display number sequences must not be dropped by automatic rollback; use the release recovery plan.");
        }
    }
}
