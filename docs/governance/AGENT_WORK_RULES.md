# Agent Work Rules

## Repository Safety

- Do not modify `D:\guli\gulierp`.
- Read old project docs only for knowledge extraction.
- Do not migrate Admin.NET.
- Do not create Admin.NET adapters.
- Do not copy legacy Sales, Purchase or Inventory code.
- Do not write to the GuliERP Next PostgreSQL database (`192.168.2.228:5432`,
  `gulierp_g2_003_test`). As of 2026-10-25 it is in active use by other
  development tasks. No `INSERT` / `UPDATE` / `DELETE`, no
  `dotnet ef database update`, no seed execution, and no test or harness run
  that mutates it. Read-only inspection only, under the Wrong-DB Guard in
  `docs/governance/DATABASE_TARGET_REGISTRY.md` §5.1.
- Do not write to the Onlyit reference source (`D:\guli\oit\db\*.mdb`,
  `D:\guli\oit\backups\*.mdb`) and do not call Onlyit write services
  (`sf_*_save`, `sf_voucher_create`, `sf_voucher_oper`). Onlyit write
  services do not reject missing parameters — they execute the save path
  with empty values and persist rows. Extraction from Onlyit is read-only
  (`SELECT *` via read-only ADO, or `sf_run_sql` with `sqlcmd`).

## Single Writer Areas

Only the main execution agent writes:

- Solution/project structure.
- Governance documents.
- Shared schemas and foundation boundaries.
- Goal registry updates.

Parallel agents may only perform read-only analysis unless explicitly assigned
a scoped write.

## Business Work Rules

- Discovery documents may list facts and open questions.
- Do not invent business rules that the user has not confirmed.
- No formal Sales/Purchase/Inventory page may be created before
  `USER_UX_APPROVED`.

## Verification Rules

Run or report status for:

- Backend build.
- Backend tests.
- Frontend install/build.
- Git diff check.

