# Guli Platform V1.0 — WIP Task Register

**Purpose**: account for every piece of unfinished or excluded work so that nothing is silently discarded.
**Compiled**: 2026-11-05 (UTC 03:00Z) from the live working tree, the frozen release-candidate pack R242b, and the Codex release-readiness report of 2026-11-05 07:55 (local).

**Classification key**
- **RELEASE** — shipped in V1.0 (committed in `527eb08` / `6f2c90f` / `90f8737`).
- **DEFERRED** — deliberately not in the V1.0 commit; preserved and tracked here.
- **BLOCKED** — cannot proceed without an external decision, credential, or privilege.

---

## 1. Work excluded from the V1.0 commit (DEFERRED — nothing was discarded)

| # | Item | Files | Status | Dependency | Recommended next action |
|---|---|---|---|---|---|
| W-01 | Agent scratch reports for the CRM owner/identity work | `outputs/MINIMAX_R39_IDENTITY_PROMOTION_READONLY_REVIEW.md`, `…R41_OWNER_ASSIGNMENT_READONLY_REVIEW.md`, `…R42_OWNER_HTTP_GUARD_IMPLEMENTATION.md`, `…R43_OWNER_HTTP_TEST_REPORT.md`, `…R44_SOURCE_READONLY_REVIEW.md`, `…R46_SYNTHETIC_HTTP_PLAN.md` | Untracked, 6 files, preserved in `backup/ignored`-equivalent `worktree/` of the release backup | None | Promote the two that carry durable findings (R42 implementation notes, R46 gap matrix) into `docs/verification/`, then delete `outputs/`. |
| W-02 | Development-only API configuration | `apps/api/GuliERP.Api/appsettings.Development.json` | Untracked. Points at the **protected** `gulierp_g2_003_test` with a `CHANGE_ME` placeholder | Release of the `gulierp_g2_003_test` hold (see W-07) | Keep untracked until the hold is lifted; it must not ship pointing at a protected DB. |
| W-03 | One-off diagnostic probes written during this release | `tools/dev/probe-live-sites.ps1`, `tools/dev/probe-site-content.ps1` | Untracked, kept only in the backup | None | Discard or promote to `tools/dev/` as a live-site health probe — a judgement call for the next iteration. |
| W-04 | Stray tracked artefact at the repository root | `gulierp-next` (16,812 B — a captured `git diff` dump) | **Tracked in Git**, unmodified, therefore shipped in the release commit | Approval to delete | Delete with an explicit approval; it is repository debt, not product content. Not removed by this release because destructive cleanup needs consent. |

---

## 2. Deferred ERP-surface changes carried inside the release (RELEASE, but flagged)

| # | Item | Files | Status | Note |
|---|---|---|---|---|
| W-05 | UoM active-status normalisation | `apps/web/src/components/transaction/LineGrid.vue` (+7/−7), `apps/web/src/views/sales-order/SalesOrderEdit.vue` (+1/−1) | Shipped | Part of the frozen candidate. Excluding them would have broken the "HEAD rebuilds the released artifact" guarantee. Behaviour is additive (case-insensitive `'active'`), no contract change. |
| W-06 | Whitespace nits in the candidate | `modules/crm/…/20261022100000_ConvertLeadAssessmentCategoryToBusinessCategory.Designer.cs` (blank line at EOF), `tests/GuliERP.Crm.Tests/ContactCandidateFacts.cs` (blank line at EOF), `modules/crm/…/Services/QuakeRuntimeCredentialStore.cs:54` (trailing space) | Shipped as-is | Left byte-identical to the candidate on purpose. 3 findings from `git diff --check`; cosmetic only. |

---

## 3. Release gates inherited from the Codex readiness review (BLOCKED)

Source: `GULIERP_CRM_V12_RELEASE_READINESS_REPORT.md`, 2026-11-05 07:55. **Re-evaluated in this release** — see the status column.

| ID | Original status | Status after this release | Evidence | Next action / owner |
|---|---|---|---|---|
| REL-P0-01 | BLOCKED — formal DB identity, four account classes and permissions not registered | **PARTIALLY CLEARED** — the formal target exists and is server-verified (`gulierp_next_prod` @ 192.168.2.228:5432, `inet_server_addr=172.29.32.2`), 31 CRM migrations applied, 1 tenant / 1 company / 1 user / 7 roles registered | `docs/release/RELEASE_BASELINE_REPORT.md` §5 | Register the **business** account classes (CRM operator, CRM reviewer, CRM read-only) and their owners — the release currently ships the platform admin only. *Owner: release owner.* |
| REL-P0-02 | BLOCKED — formal backup dir, off-host copy, encryption, retention, recovery owner not registered | **PARTIALLY CLEARED** — a formal backup job exists and passes (`GuliERP-Next-Prod-CRM-Daily-Backup` / `-Weekly-Backup`, `D:\backup\gulierp_next_prod`, last run 2026-11-05 02:10, result 0); this release adds a verified release-grade backup plus a **proven restore** | `docs/release/BACKUP_VERIFICATION_REPORT.md` §4.1 | Still open: off-host copy is declined (single-site loss risk, accepted), encryption at rest not configured, recovery owner (`admin`) not re-confirmed. *Owner: release owner / administrator.* |
| REL-P0-03 | BLOCKED — formal target did not exist | **CLEARED** — target exists, schema inventoried, 68 base tables, migration history matches the candidate | Baseline report §5 | Keep the pre-flight read-only assertion in the deploy script. *Owner: database executor.* |
| REL-P1-01 | BLOCKED — 207 uncommitted changes; source review matrix incomplete | **CLEARED for the release** — the commit reproduces the frozen candidate **1,371/1,371 byte-identical, 0 drift**; the source-level audit burden is discharged by that equivalence plus the 255/107 test runs | `evidence/commit-candidate-equivalence.json` | Residual: file-level review of the 143 previously-unattributed new files is still desirable before V1.1. *Owner: review owner.* |
| REL-P1-02 | BLOCKED — permission matrix covered three representative roles only; cross-tenant / cross-company not covered | **PARTIALLY CLEARED** — cross-company and cross-tenant isolation are enforced by global query filters + scope guards and are covered by automated tests; the R237 rehearsal additionally proved 403 on cross-company switch and CRM read rejection for a no-permission account | CRM test suite 255/255; R237 self-hosted HTTP evidence | Still open: an end-to-end browser matrix across all three CRM roles against the **live** deployment. *Owner: acceptance owner.* |
| REL-P1-03 | BLOCKED — stale execution checklist | **CLEARED** — this release is driven by the current R242b candidate and by the four documents in `docs/release/` | this document set | — |
| REL-P1-04 | PASS (isolated only) — global CRM number sequences | **CLEARED for V1.0** — the formal DB carries `20261027092759_AddGlobalCrmNumberSequences`; the formal DB has 0 CRM rows, so the tail-9999 → 10000 upgrade path does not apply | Baseline report §5 | Re-verify sequencing once real business data exists. *Owner: application ops.* |
| REL-P1-05 | BLOCKED — runtime role's `USAGE` on `crm.company_no_seq` / `crm.deal_no_seq` not verified | **PARTIALLY CLEARED** — the application currently connects as `gulidata`, which owns the sequences and therefore holds `USAGE` | Baseline report §5 | Still open: introduce and verify a **low-privilege runtime role** as the registry §8 planned. *Owner: database executor.* |
| REL-P1-06 | PASS (isolated empty DB only) — 53/53 migration rehearsal | **CLEARED for V1.0** — the formal DB already carries all 31 CRM migrations; **no migration is executed by this release** | Baseline report §5 | Keep the migration SQL manifest reviewed for the next schema change. *Owner: database executor.* |
| REL-P2-01 | BLOCKED / deferrable — Quake / 站长之家 supplier authorisation | **DEFERRED BY DESIGN** — the switches stay off for first release | — | Obtain supplier credentials, then re-enable behind the per-source authorisation model. *Owner: application ops.* |

---

## 4. Known environment and governance gaps

| # | Gap | Severity | Status | Next action |
|---|---|---|---|---|
| W-07 | `gulierp_g2_003_test` is under an **ACTIVE-USE HOLD** (user instruction 2026-10-25). It was not written, not dumped and not migrated. | — | Open | Confirm whether the hold still applies. *Owner: release owner.* |
| W-08 | `docs/governance/DATABASE_TARGET_REGISTRY.md` §8 is **stale**: it states `gulierp_next_prod` is "not created" and the release gate "BLOCKED". The database exists, is migrated and is covered by a running backup job. | P1 | Open | Rewrite §8 to the verified reality and re-register the formal gate. *Owner: governance owner.* |
| W-09 | The R243 **IIS recovery rehearsal** (`R243-Elevated-IISRecoveryGate.ps1`) is still **locked at the script entry** (`-Execute` refused) and has never been run by an administrator. Its cleanup path is therefore unproven. | P1 | Open | Run the isolated failure-cleanup rehearsal as administrator on the free port 8184; the script and its evidence pack already exist. *Owner: administrator.* |
| W-10 | This session runs **unelevated**, so IIS site / app-pool / service-identity management is impossible from it (`appcmd` → *insufficient permissions*). | — | Constraint | Any IIS-level promotion must be executed by an elevated operator. |
| W-11 | Two pre-existing IIS sites on **8090 / 8091** serve an **older** "Guli Platform" build. The R240 gate forbids touching them; their ownership and intended role are undocumented in the repository. | P2 | Open | Record which site is the canonical V1.0 target, then retire or repoint the other. *Owner: application ops.* |
| W-12 | No CI/CD pipeline exists — `.github/` contains only issue and PR templates. Releases are manual. | P2 | Open | Decide whether V1.1 introduces a build workflow; the release tooling in `tools/dev/` is the seed for it. *Owner: release owner.* |
| W-13 | `.stack-pids.json` still records the pre-rename repository root. | P3 | Open | Regenerated automatically by `tools/dev/start-stack.ps1`; no action needed beyond that. |

---

## 5. What the release deliberately did **not** do

| Not done | Why |
|---|---|
| Reset, clean, checkout-overwrite or delete any tracked file | Explicitly forbidden; the working tree was preserved intact and then committed. |
| Run `dotnet ef database update` or any migration against any database | The formal target already carries the required migrations; the other databases are protected or dev-only. |
| Write to `gulierp_g2_003_test` | ACTIVE-USE HOLD. |
| Drop, rename, reset or create any database | Not required; forbidden by the release rules. |
| Delete the root file `gulierp-next` or the `outputs/` reports | Destructive cleanup requires explicit approval. |
| Modify the 3 whitespace nits | Doing so would break the candidate-equivalence guarantee for no functional gain. |
| Deploy to IIS from this session | Requires elevation; the rehearsal is still locked (W-09/W-10). |
| Rename the .NET solution, assemblies or namespaces | That is an architecture change, out of scope for a release. |
| Install any new tool | The PostgreSQL 17 client was reused from the existing Codex tool cache; `gh` was not installed — the GitHub rename used the GitHub REST API with the stored PAT. |

---

## 6. Recommended V1.1 order (one task at a time)

1. **W-08** — correct `DATABASE_TARGET_REGISTRY.md` §8 so governance reflects the real formal target and the running backup job.
2. **W-01** — promote the durable CRM reports into `docs/verification/` and clear `outputs/`.
3. **REL-P1-02 / W-11** — run the three-role end-to-end permission matrix against the live deployment, and decide the canonical IIS site.
4. **W-09** — execute the locked R243 IIS recovery rehearsal as administrator to prove the recovery path.
5. **REL-P0-01 / REL-P1-05** — register the business accounts and introduce a low-privilege runtime role.
6. **W-04 / W-13** — repository hygiene pass, with explicit approval for deletions.
