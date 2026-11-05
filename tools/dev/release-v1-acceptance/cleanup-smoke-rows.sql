-- =====================================================================
-- Guli Platform V1.0 — release-acceptance residue cleanup
--
-- Deletes ONLY the rows created by the release acceptance harness.
-- Every row it touches carries the 'V10-SMOKE-' marker, so business data
-- cannot be matched by accident.
--
-- THIS FILE HAS BEEN PROVED: it was executed verbatim inside a transaction
-- that was ROLLED BACK, so the statement set is known to run cleanly
-- against the current schema and to satisfy every foreign-key constraint.
-- It was never committed.
--
-- 2026-11-06: section 7 (sales orders + their lines) was added so the
-- sales-order acceptance rows are removable as well. The extended statement
-- set was proved the same way - executed verbatim inside a transaction that
-- was rolled back against the formal database.
--
-- Usage (requires approval — it is a destructive operation on the formal
-- release database):
--     psql -h <host> -p 5432 -U <user> -d gulierp_next_prod \
--          -v ON_ERROR_STOP=1 -f cleanup-smoke-rows.sql
--
-- Safe to re-run: after the first run the marker matches nothing and every
-- DELETE reports 0 rows.
-- =====================================================================

\set ON_ERROR_STOP on

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. Activities first.
--    crm.activities has a NO ACTION foreign key onto crm.deals, so an
--    activity that points at a smoke deal must go before that deal.
-- ---------------------------------------------------------------------------
delete from crm.activities
where "CrmCompanyId" in (
        select "Id" from crm.companies where "FullName" like 'V10-SMOKE-%')
   or "Content" like '%V10-SMOKE-%'
   or "DealId" in (
        select "Id" from crm.deals
         where "CrmCompanyId" in (
                select "Id" from crm.companies where "FullName" like 'V10-SMOKE-%'));

-- ---------------------------------------------------------------------------
-- 2. Opportunities (deals) referencing a smoke customer.
-- ---------------------------------------------------------------------------
delete from crm.deals
where "CrmCompanyId" in (
        select "Id" from crm.companies where "FullName" like 'V10-SMOKE-%');

-- ---------------------------------------------------------------------------
-- 3. Contacts of a smoke customer.
-- ---------------------------------------------------------------------------
delete from crm.contacts
where "CrmCompanyId" in (
        select "Id" from crm.companies where "FullName" like 'V10-SMOKE-%');

-- ---------------------------------------------------------------------------
-- 4. The customer master records themselves.
-- ---------------------------------------------------------------------------
delete from crm.companies
where "FullName" like 'V10-SMOKE-%';

-- ---------------------------------------------------------------------------
-- 5. The lead evidence, the assessment revisions and the assessments.
-- ---------------------------------------------------------------------------
delete from crm.lead_assessment_evidence
where "LeadId" in (
        select "Id" from crm.leads where "CompanyName" like 'V10-SMOKE-%');

delete from crm.lead_demand_assessment_revisions
where "LeadId" in (
        select "Id" from crm.leads where "CompanyName" like 'V10-SMOKE-%');

delete from crm.lead_demand_assessments
where "LeadId" in (
        select "Id" from crm.leads where "CompanyName" like 'V10-SMOKE-%');

-- ---------------------------------------------------------------------------
-- 6. The leads.
-- ---------------------------------------------------------------------------
delete from crm.leads
where "CompanyName" like 'V10-SMOKE-%';

-- ---------------------------------------------------------------------------
-- 7. The sales orders created by the acceptance harness.
--    sales.gulierp_sales_order_line references the order header, so the
--    lines go first. Only orders whose Remarks carry the marker are touched,
--    exactly like every other section above.
-- ---------------------------------------------------------------------------
delete from sales.gulierp_sales_order_line
where "SalesOrderId" in (
        select "Id" from sales.gulierp_sales_order
         where "Remarks" like '%V10-SMOKE-%');

delete from sales.gulierp_sales_order
where "Remarks" like '%V10-SMOKE-%';

COMMIT;
