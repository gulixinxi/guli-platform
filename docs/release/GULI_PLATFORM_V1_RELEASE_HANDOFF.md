# Guli Platform V1.0 — Release Handoff

**Product**: 谷粒信息平台 (Guli Platform)
**Repository**: `gulixinxi/guli-platform` (GitHub, public) · local `D:\guli\projects\guli-platform`
**Release commit**: `69b79b8` on `master` · **release candidate commit**: `527eb08`
**Date (UTC)**: 2026-11-05
**Executor**: MiniMax (primary). Independent verification: Codex release-candidate pack R242b (read-only), plus in-release independent re-verification.

---

## 1. Release status

| # | Item | Status |
|---|---|---|
| 1 | Source released and reproducible | ✅ **GO** — the **release commit** `527eb08` reproduces the frozen R242b candidate **1,371 / 1,371 files byte-identical (SHA-256), 0 drift**. Scope note: 10 of those candidate files were **intentionally changed by later commits** (rename-proof paths, npm package identity, the §8 governance correction) — the current HEAD therefore reproduces the candidate's *content* but not its original *bytes*; see §4 |
| 2 | Build | ✅ **GO** — `dotnet build -c Release`: **0 warnings, 0 errors** (38 projects); `npm run build` (`vue-tsc -b`): **PASS** |
| 3 | Automated tests | ✅ **GO** — CRM **255/255**, API **107/107** |
| 4 | Credential hygiene | ✅ **GO** — 16 scan candidates adjudicated in context, **0 real secrets** committed |
| 5 | Backup | ✅ **GO** — out-of-tree backup, 1,849-entry SHA-256 manifest, verified git bundle, 2 read-only DB archives |
| 6 | Restore proven | ✅ **GO** — formal-target dump restored into an isolated local cluster: **68/68 tables, 73/73 rows, 0 mismatch**, no NAS write |
| 7 | GitHub identity | ✅ **GO** — `gulixinxi/gulierp-next` → **`gulixinxi/guli-platform`**; old URL redirects; `master` = `69b79b8` |
| 8 | Local identity | ✅ **GO** — `D:\guli\projects\gulierp-next` → **`D:\guli\projects\guli-platform`**; remote, package identity and script paths updated; fetch / push / build verified from the new path |
| 9 | Deployment | ✅ **GO** — live on `http://192.168.2.200:8080` against the formal database `gulierp_next_prod`; production start-up gate passed; **11/11** unauthenticated surface checks PASS |
| 10 | Authenticated CRM business flow | ✅ **GO** — executed end to end against the live deployment: **16/16 checks PASS** (login → customer → follow-up → lead → confirmed demand assessment → lead conversion → opportunity → persistence read-back → cross-company boundary). The verification operator is retired and its login is **proven** rejected with HTTP 401 — see §5 |
| 11 | IIS promotion | ⏸ **DEFERRED** — this session is unelevated; the two existing IIS sites (8090 / 8091) were left untouched, as the R240 gate requires |

### Overall verdict

**GO for business availability.** The released software, its identity, its backup/restore posture, a live, database-connected deployment **and** the authenticated end-to-end business flow are all verified. What remains open is the cleanup of the acceptance residue (R-12) and administrator-only work (R-04, R-05) — none of it a release blocker.

---

## 2. Deployment

| Item | Value |
|---|---|
| **Deployment URL (LAN)** | **http://192.168.2.200:8080/** |
| Deployment URL (this host) | http://127.0.0.1:8080/ |
| Form | Self-hosted ASP.NET Core (Kestrel), `ASPNETCORE_ENVIRONMENT=Production` |
| Artifact root | `D:\guli\platform-deploy\v1.0` (outside the repository) |
| API binary | `api\GuliERP.Api.dll` — SHA-256 `49AB10B9CC19E7DBC0628B8EE37D4BD105A5F966AC406BA12B7DCC6963F2D58F` |
| SPA build | `api\wwwroot\` — `index.html` SHA-256 `0490D3D55EDDE827C895BC6CB6FB9C86B8B202531CEE300E22500BF486953F00` (identical to `apps/web/dist/index.html`) |
| Artifact manifest | `D:\guli\platform-deploy\v1.0\deploy-manifest.csv` — 127 files, SHA-256 each |
| Database | `gulierp_next_prod` @ `192.168.2.228:5432`, server-reported address `172.29.32.2` |
| DB password source | Windows Credential Manager target `GuliERP-Next-Prod-Backup-gulidata` — **never written to disk** |
| Production gate | `ReleaseDatabaseTargetGuard` asserted host / port / database / user **and** the server-reported identity before start-up; the instance refuses to run against any other target |
| Logs | `D:\guli\platform-deploy\v1.0\logs\` |
| Start / stop | `start-guli-platform.ps1` / `stop-guli-platform.ps1` (the stop script also prints the rollback position) |

### 2.1 Verified behaviour of the live instance (11/11 PASS)

| Check | Result |
|---|---|
| `/health/live` | 200 Healthy |
| `/health/ready` | 200 Healthy — **real database round-trip** (`SELECT 1 OK against Host=192.168.2.228`) |
| SPA shell `/` | 200, title `Guli Platform · 谷粒业务平台` |
| SPA deep link `/crm/companies` | 200 — client-side routing falls back to the shell |
| `/api/v1/auth/csrf` | 200, header `X-CSRF-TOKEN` |
| anonymous `/api/v1/crm/leads` / `companies` / `deals` | **401** — the CRM surface is not anonymous |
| anonymous `/api/v1/auth/me` | **401** |
| LAN reachability `192.168.2.200:8080` | 200 |
| `X-Tenant-Id` / `X-Company-Id` / `X-User-Id` override headers | **401** — the Testing-only claim override is correctly inactive in Production |

### 2.2 Artifact provenance

Against the frozen R242b candidate deploy package (103 files): **82 byte-identical**; the 21 that differ are exactly the project's own assemblies, and the difference is **build-path metadata**, not code:

- assembly identity identical — `GuliERP.Api`, version `1.0.0.0` in both;
- size delta 484,864 → 484,352 B = 512 B, the length of the different embedded build path;
- the deployed DLL embeds `D:\guli\projects\guli-platform\apps\api\GuliERP.Api\obj\Release\net10.0\GuliERP.Api.pdb`, the package DLL embeds its own path;
- the **source** behind both is byte-identical (1,371/1,371).

`appsettings.Development.json` was removed from the production artifact: it names the protected `gulierp_g2_003_test` database and is never loaded in Production, but it has no business shipping.

---

## 3. Identity changes

| Surface | Before | After |
|---|---|---|
| GitHub repository | `gulixinxi/gulierp-next` | **`gulixinxi/guli-platform`** (old URL redirects; verified) |
| Remote | `…/gulierp-next.git` | **`https://github.com/gulixinxi/guli-platform.git`** (fetch ✅ push ✅) |
| Local directory | `D:\guli\projects\gulierp-next` | **`D:\guli\projects\guli-platform`** |
| npm package | `@gulierp-next/web` | **`@guli-platform/web`** (package.json + lock in sync) |
| UI title | `GuliERP · 销售订单 UX 原型(G1B-1)` | **`Guli Platform · 谷粒业务平台`** (already present in the release candidate) |
| Script paths | hard-coded `D:\guli\projects\gulierp-next` | resolved from `$PSScriptRoot` — the tree is now move-safe |

**Deliberately not renamed**: the .NET solution, assemblies and namespaces (`GuliERP.*`), the `crm.*` / `identity.*` schema and permission codes. Renaming those is an architecture change and a database migration, not a release.

### 3.1 CI/CD, webhooks and deployment integrations — checked

| Item | Finding |
|---|---|
| GitHub Actions / CI workflows | **None exist.** `.github/` contains only issue and PR templates. Releases are manual. |
| Webhooks | **None** configured on the repository (verified via the GitHub API before renaming). |
| Deploy keys | None (API returned 404 for the endpoint). |
| Collaborators | 1 — `gulixinxi` (admin). |
| Other runtime references to the old repo URL | None in functional configuration; the GitHub redirect keeps the old URL working for any external reference. |
| Branch protection / environments | None configured. |

---

## 4. Verification evidence

| Layer | Evidence |
|---|---|
| Source ↔ candidate | `evidence/commit-candidate-equivalence.release-527eb08.json` — at the **release commit** `527eb08`: 1,371/1,371 byte-identical to the manifest, 0 mismatch, 0 line-ending drift. The live file `evidence/commit-candidate-equivalence.json` re-runs the same proof at the **current HEAD**. The commit also carries **7 pre-existing tracked files outside the candidate** (listed verbatim in that evidence file: `appsettings.Development.json`, the root `gulierp-next` dump, and 5 `tools/dev/` release scripts) — none of them product code from the candidate |
| Candidate drift after the release commit | **Exactly 10** candidate files were changed by the commits that followed `527eb08`, and the final verification pins that set so any *new* drift fails loudly: `apps/web/package.json`, `apps/web/package-lock.json` (package identity → `@guli-platform/web`), `tools/dev/check-runtime.ps1`, `tools/dev/g2-005-operator-evidence.ps1`, `tools/dev/mdm-001-final-acceptance.ps1`, `tools/dev/mdm-001-final-acceptance-selftest.ps1`, `tools/dev/mdm-001-operator-evidence.ps1`, `tools/dev/diagnose-admin-role-binding.ps1`, `tools/dev/run-web-preview-backend.ps1` (all: hard-coded paths → `$PSScriptRoot`), `docs/governance/DATABASE_TARGET_REGISTRY.md` (§8 corrected). No product logic, schema or migration was touched |
| Build | Release build 0 warnings / 0 errors, from **both** the original and the renamed directory |
| Tests | CRM 255/255, API 107/107 (re-run from the renamed directory) |
| Frontend | `vue-tsc -b` + Vite production build PASS |
| Credential scan | `tools/dev/baseline-sensitive-scan.py` — 1,361 files, 16 candidates, 0 real secrets |
| Backup | `BACKUP_VERIFICATION_REPORT.md` §1–§5 |
| Restore | `VERIFY-restore.json` — pg_restore exit 0, 68/68 tables, 73/73 rows, `NASWritePerformed=false` |
| Deployment | `verify-deploy-surface.ps1` — 11/11 PASS |
| Authenticated business flow | `run-release-acceptance.ps1` — **16/16 PASS** + retired-operator login rejected **HTTP 401** (§5) |
| Business-flow persistence | re-read directly from PostgreSQL: the opportunity row carries the confirmed assessment id + version; the evidence row carries its type and verification status (§5) |
| Database | 68 base tables, 73 rows, 31 CRM migrations, 1 tenant / 1 company / 1 user / 7 roles |

---

## 5. CRM business smoke test — executed and verified

**Status: ✅ PASS — 16 / 16 checks**, executed against the live deployment `http://192.168.2.200:8080` against the formal database `gulierp_next_prod`.

The authenticated chain `login → customer → follow-up → lead → confirmed demand assessment → lead conversion → opportunity → persistence read-back → cross-company boundary` was run end to end.

| # | Check | Result |
|---|---|---|
| 1 | `POST /api/v1/auth/login` | HTTP 200 |
| 2 | `GET /api/v1/auth/me` | HTTP 200 — user / tenant `GULIXINXI` / company `GULIXINXI` |
| 3 | CRM permission codes present | 13 codes, including `crm.company.manage`, `crm.activity.manage`, `crm.deal.manage`, `crm.lead.convert` |
| 4 | `POST /api/v1/crm/companies` | HTTP 201 |
| 5 | `POST /api/v1/crm/activities` | HTTP 201 |
| 6 | `POST /api/v1/crm/leads` | HTTP 201 |
| 7 | `PUT /api/v1/crm/leads/{id}/demand-assessments/website` | HTTP 200 — state `confirmed`, one structured evidence item |
| 8 | `GET …/demand-assessments` (read-back) | HTTP 200 — `state=confirmed`, 1 evidence item |
| 9 | `POST /api/v1/crm/leads/{id}/convert` | HTTP 200 — customer master record created |
| 10 | `POST /api/v1/crm/deals` | HTTP 201 — opportunity bound to the confirmed assessment **and its version** |
| 11 | `GET` read-back of all five created records | HTTP 200 ×5 |
| 12 | `POST /api/v1/auth/company/switch` to a foreign company | HTTP 400 — rejected |

### 5.1 The business rules were exercised, not bypassed

The opportunity gate in `DealService.CreateAsync` refuses a deal that does not carry a confirmed assessment id **and** its current version, and refuses one whose customer did not come from the source lead (`lead.ConvertedToCompanyId != deal.CrmCompanyId`). A run that creates the customer and the lead independently therefore *cannot* produce an opportunity. The passing result proves the real path **`线索 → 需求评估 → 转化 → 商机`**, not a back door.

### 5.2 Persisted state re-read from PostgreSQL, not from the API

`DealDto` does not expose the assessment linkage, so the write path was verified independently by reading the database directly:

| Row | Verified content |
|---|---|
| `crm.deals` | **2** opportunities, **both** bound to a confirmed assessment id (`1` and `2`) and its version (`1`), `Category = 0` (企业建站), `ExpectedAmount = 58000`, and **both** on the **converted** customer, not the directly created one |
| `crm.lead_demand_assessments` | `State = confirmed`, `Confidence = high`, `Version = 1`, bound to the smoke leads |
| `crm.lead_assessment_evidence` | `EvidenceType = manual`, `VerificationStatus = verified`, with the marker in the note |

### 5.3 Credential handling

The business account `wangchunqing` was **never touched** — no reset, no lockout, no role change. The flow ran under a dedicated, clearly-named, self-retiring operator `GULIXINXI_v10_verify_operator`, provisioned through the project's own Identity Bootstrap CLI with the `CRM_OPERATOR` role. Its password was generated in-process, never printed, never written to disk and never passed on a command line; the harness receives it through an environment variable.

### 5.4 Retiring the operator — and the defect it exposed

The first version of the retirement statement set `LockoutEnabled = true, LockoutEnd = null` and called `newid()`. That is wrong twice over:

1. **`newid()` is a SQL Server function.** PostgreSQL does not have it, so the whole `UPDATE` failed and **silently changed nothing** — the two `DELETE`s that followed still succeeded, which made it look as if the retirement had worked.
2. **Even had it run, it would not have blocked login.** ASP.NET Core Identity treats an account as locked out only when `LockoutEnabled && LockoutEnd > now`; a NULL `LockoutEnd` leaves the account authenticable. The login path rejects only `Status != UserStatus.Active` (`AuthenticationService.LoginAsync`), and the account was left at `Status = 1` — **still Active**.

The retirement now sets `Status = 2` (`UserStatus.Disabled`), arms the lockout with a future `LockoutEnd`, rotates the security stamp and removes the role-assignment and company-membership rows. The harness then **re-attempts the login with the still-known password and requires HTTP 401** — a retirement that cannot be demonstrated by a real attempt is not treated as a retirement.

Final verified state: `Status = 2`, `LockoutEnabled = true`, `LockoutEnd` set, role assignments **0**, company memberships **0**, and the business account `wangchunqing` unchanged (`Status = 1`).

### 5.5 Smoke-test rows left in the formal database

Every record the harness creates carries a unique `V10-SMOKE-<timestamp>` marker so the rows are identifiable and removable. The executed runs left these rows in `gulierp_next_prod`:

| Marker | Rows created |
|---|---|
| `V10-SMOKE-20261105112622` | 1 customer |
| `V10-SMOKE-20261105112751` | 1 customer, 1 lead, 1 follow-up |
| `V10-SMOKE-20261105113302` | 1 customer, 1 lead, 1 converted customer, 1 opportunity, 2 follow-ups |
| `V10-SMOKE-20261105113547` | 1 customer, 1 lead, 1 converted customer, 1 opportunity, 2 follow-ups |

**They were deliberately not deleted.** Removing rows from the formally approved release database is a destructive, irreversible operation, and the release rules require explicit approval for that. A ready-to-run, marker-scoped cleanup statement is provided in `WIP_TASK_REGISTER.md` (W-14).

### 5.6 The harness

| Item | Path |
|---|---|
| Smoke test | `tools/dev/release-v1-acceptance/smoke-test-crm.ps1` (also `D:\guli\platform-deploy\v1.0\smoke-test-crm.ps1`) |
| One-command acceptance | `tools/dev/release-v1-acceptance/run-release-acceptance.ps1` — provision → smoke → retire → **prove the retirement** |
| Consolidated verification | `tools/dev/release-v1-acceptance/final-verification.ps1` — **33 checks** over git identity, working tree, candidate drift, the four documents, the live deployment, the CRM acceptance, the database state and the backup manifest. Latest result: **33 passed, 0 failed** (`logs\final-verification.json`) |
| Cleanup SQL (proved, not applied) | `tools/dev/release-v1-acceptance/cleanup-smoke-rows.sql` |
| Result log | `logs/smoke-result.csv` |
| Operator retirement repair (run once, already applied) | `D:\guli\platform-deploy\v1.0\logs\retire-verification-operator.ps1` |
| Read-only row inspection | `D:\guli\platform-deploy\v1.0\logs\inspect-smoke-rows.ps1` |

---

## 6. Remaining P2 / P3 issues (none blocks the release)

| ID | Severity | Issue | Next action |
|---|---|---|---|
| R-01 | P2 | The authenticated CRM business flow | **CLOSED in this release** — executed against the live deployment, **16/16 PASS**, persisted state re-verified in PostgreSQL, and the retired operator proven unable to log in (§5) |
| R-02 | P2 | Third-party data sources (Quake, 站长之家) are not authorised against real supplier accounts | Keep the switches **off** for the first release; obtain supplier credentials, then re-enable behind the per-source authorisation model |
| R-03 | P2 | `docs/governance/DATABASE_TARGET_REGISTRY.md` §8 was stale — it still said `gulierp_next_prod` is "not created" and the gate BLOCKED | **CLOSED in this release** — §8 rewritten to the verified reality (database exists, migrated, backed up, serving the release) |
| R-04 | P2 | The R243 IIS recovery rehearsal has never been run by an administrator (`-Execute` is refused at the entry) | Run it as administrator on the free port 8184; the failure-cleanup path is still unproven |
| R-05 | P2 | Two pre-existing IIS sites (8090 / 8091) serve an older build and their intended role is undocumented | Decide the canonical IIS target, then retire or repoint the other |
| R-06 | P2 | No CI/CD pipeline; releases are manual | `tools/dev/release-v1-backup.ps1` and the verification scripts are the seed for one |
| R-07 | P2 | The application connects as `gulidata`, which owns the CRM number sequences | Introduce the low-privilege runtime role the registry §8 planned and verify `USAGE` on `crm.company_no_seq` / `crm.deal_no_seq` |
| R-08 | P3 | 3 whitespace nits reported by `git diff --check` (2 generated EF designer files, 1 trailing space) | Left byte-identical to the frozen candidate on purpose; clean up in a dedicated hygiene commit |
| R-09 | P3 | The root file `gulierp-next` (16 KB, a captured `git diff`) is tracked in Git | Delete only with explicit approval |
| R-10 | P3 | `outputs/` holds 6 agent scratch reports; the two `tools/dev/probe-*.ps1` scripts are untracked; `appsettings.Development.json` is tracked and shipped but excluded from the deployment artifact | Promoted into `docs/verification/` and cleared in the next iteration; all preserved in the release backup |
| R-11 | P3 | `.stack-pids.json` still records the pre-rename repository root | Regenerated automatically by `tools/dev/start-stack.ps1` |
| R-12 | P2 | The acceptance left marker-tagged rows (`V10-SMOKE-*`) in the formal CRM database | Approve or run the marker-scoped cleanup in `WIP_TASK_REGISTER.md` (W-14) — a destructive operation that requires explicit approval |

Full task-by-task accounting: `WIP_TASK_REGISTER.md`.

---

## 7. Rollback

| Step | Action |
|---|---|
| 1 | Stop the release instance: `pwsh D:\guli\platform-deploy\v1.0\stop-guli-platform.ps1` |
| 2 | The previous deployment was never touched — the IIS sites on `http://127.0.0.1:8090` and `:8091` are still healthy and serving the previous build |
| 3 | If the database must be rolled back: restore `D:\backup\guli-platform\guli-platform-v1-20261105T024244Z\db\guli-platform-v1-gulierp_next_prod-20261105T024244Z.dump` (SHA-256 `B0321BD2…`) into a **new** database with `pg_restore --exit-on-error`, verify 68 tables / 73 rows, then repoint `ConnectionStrings__GuliERP` |
| 4 | Never overwrite an existing backup or drop a database; the pre-existing formal daily/weekly series in `D:\backup\gulierp_next_prod` is intact |

---

## 8. Next iteration priorities

1. **Decide on the smoke-test residue** (R-12 / W-14) — approve and run the marker-scoped cleanup of the `V10-SMOKE-*` rows in the formal CRM database; then re-run `tools/dev/release-v1-acceptance/run-release-acceptance.ps1` so the flow stays green.
2. **Promote the durable CRM reports out of `outputs/`** and clear the scratch directory (R-10).
3. **Run the three-role permission matrix against the live deployment** — `CRM_OPERATOR`, `CRM_IDENTITY_REVIEWER`, `CRM_READONLY` — and record which company/tenant combinations are covered.
4. **Decide the canonical IIS target** and run the locked recovery rehearsal as administrator (R-04, R-05).
5. **Introduce the low-privilege runtime database role** (R-07), then the first real customer record, and only then consider real business data.
6. **Authorise the third-party data sources** (R-02) behind their per-source authorisation model.
8. **Repository hygiene pass** with explicit approval for deletions (R-08, R-09, R-11).

---

## 9. Where everything is

| Item | Path |
|---|---|
| Repository (canonical) | `D:\guli\projects\guli-platform` |
| GitHub | https://github.com/gulixinxi/guli-platform |
| Deployment | `D:\guli\platform-deploy\v1.0` (+ `deploy-manifest.csv`, `logs\`, `start-/stop-guli-platform.ps1`, `verify-deploy-surface.ps1`, `smoke-test-crm.ps1`, `run-release-acceptance.ps1`) |
| Release backup | `D:\backup\guli-platform\guli-platform-v1-20261105T024244Z` (1,849 files under SHA-256) |
| Formal database backups | `D:\backup\gulierp_next_prod` (daily + weekly, last run 2026-11-05 02:10, result 0) |
| Release documents | `docs/release/RELEASE_BASELINE_REPORT.md`, `docs/release/BACKUP_VERIFICATION_REPORT.md`, `docs/release/WIP_TASK_REGISTER.md`, `docs/release/GULI_PLATFORM_V1_RELEASE_HANDOFF.md` |
| Release tooling | `tools/dev/release-v1-backup.ps1`, `tools/dev/release-v1-restore-verify.ps1`, `tools/dev/verify-commit-matches-candidate.ps1`, `tools/dev/compare-candidate-scope.ps1`, `tools/dev/group-release-scope.ps1`, `tools/dev/review-secret-candidates.ps1` |
| Acceptance harness (in the repository) | `tools/dev/release-v1-acceptance/smoke-test-crm.ps1`, `tools/dev/release-v1-acceptance/run-release-acceptance.ps1` |
