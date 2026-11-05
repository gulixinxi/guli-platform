using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuliERP.Crm.Infrastructure.Migrations;

public partial class ConvertLeadAssessmentCategoryToBusinessCategory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            BEGIN
              IF EXISTS (SELECT 1 FROM crm.lead_demand_assessments
                         WHERE "Category" IS NULL OR "Category" NOT IN ('website','geo','social_media','foreign_trade'))
                 OR EXISTS (SELECT 1 FROM crm.lead_demand_assessment_revisions
                            WHERE "Category" IS NULL OR "Category" NOT IN ('website','geo','social_media','foreign_trade'))
                 OR EXISTS (SELECT 1 FROM crm.lead_assessment_evidence
                            WHERE "Category" IS NULL OR "Category" NOT IN ('website','geo','social_media','foreign_trade'))
              THEN
                RAISE EXCEPTION 'B2 category conversion stopped: unknown or null category';
              END IF;
            END $$;
            """);

        foreach (var table in new[] {
            "lead_demand_assessments",
            "lead_demand_assessment_revisions",
            "lead_assessment_evidence"
        })
        {
            migrationBuilder.Sql($"""
                ALTER TABLE crm."{table}"
                ALTER COLUMN "Category" TYPE integer
                USING CASE "Category"
                    WHEN 'website' THEN 0
                    WHEN 'geo' THEN 3
                    WHEN 'social_media' THEN 8
                    WHEN 'foreign_trade' THEN 9
                    ELSE NULL
                END;
                ALTER TABLE crm."{table}"
                ADD CONSTRAINT "CK_{table}_Category_B2"
                CHECK ("Category" IN (0,3,8,9));
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] {
            "lead_assessment_evidence",
            "lead_demand_assessment_revisions",
            "lead_demand_assessments"
        })
        {
            migrationBuilder.Sql($"""
                DO $$
                BEGIN
                  IF EXISTS (SELECT 1 FROM crm."{table}"
                             WHERE "Category" IS NULL OR "Category" NOT IN (0,3,8,9))
                  THEN
                    RAISE EXCEPTION 'B2 category rollback stopped: unknown or null category';
                  END IF;
                END $$;
                ALTER TABLE crm."{table}" DROP CONSTRAINT "CK_{table}_Category_B2";
                ALTER TABLE crm."{table}"
                ALTER COLUMN "Category" TYPE character varying(30)
                USING CASE "Category"
                    WHEN 0 THEN 'website'
                    WHEN 3 THEN 'geo'
                    WHEN 8 THEN 'social_media'
                    WHEN 9 THEN 'foreign_trade'
                    ELSE NULL
                END;
                """);
        }
    }
}
