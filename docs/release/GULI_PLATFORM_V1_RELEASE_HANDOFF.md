# Guli Platform V1.0 — Release Handoff

**Product**: 谷粒信息平台 (Guli Platform)
**Repository**: `gulixinxi/guli-platform` (GitHub, public) · local `D:\guli\projects\guli-platform`
**Release commit**: `c477959` on `master` · **release candidate commit**: `527eb08`
**Date (UTC)**: 2026-11-05
**Executor**: MiniMax (primary). Independent verification: Codex release-candidate pack R242b (read-only), plus in-release independent re-verification.

---

## 1. Release status

| # | Item | Status |
|---|---|---|
| 1 | Source released and reproducible | ✅ **GO** — commit `527eb08` reproduces the frozen R242b candidate **1,371 / 1,371 files byte-identical (SHA-256), 0 drift** |
| 2 | Build | ✅ **GO** — `dotnet build -c Release`: **0 warnings, 0 errors** (38 projects); `npm run build` (`vue-tsc -b`): **PASS** |
| 3 | Automated tests | ✅ **GO** — CRM **255/255**, API **107/107** |
| 4 | Credential hygiene | ✅ **GO** — 16 scan candidates adjudicated in context, **0 real secrets** committed |
| 5 | Backup | ✅ **GO** — out-of-tree backup, 1,849-entry SHA-256 manifest, verified git bundle, 2 read-only DB archives |
| 6 | Restore proven | ✅ **GO** — formal-target dump restored into an isolated local cluster: **68/68 tables, 73/73 rows, 0 mismatch**, no NAS write |
| 7 | GitHub identity | ✅ **GO** — `gulixinxi/gulierp-next` → **`gulixinxi/guli-platform`**; old URL redirects; `master` = `c477959` |
| 8 | Local identity | ✅ **GO** — `D:\guli\projects\gulierp-next` → **`D:\guli\projects\guli-platform`**; remote, package identity and script paths updated; fetch / push / build verified from the new path |
| 9 | Deployment | ✅ **GO** — live on `http://192.168.2.200:8080` against the formal database `gulierp_next_prod`; production start-up gate passed; **11/11** unauthenticated surface checks PASS |
| 10 | Authenticated CRM business flow | ⏳ **PENDING the credential decision** — see §5. The prod admin password was entered interactively by the operator and is stored nowhere an agent can read; it was **not** reset without approval |
| 11 | IIS promotion | ⏸ **DEFERRED** — this session is unelevated; the two existing IIS sites (8090 / 8091) were left untouched, as the R240 gate requires |

### Overall verdict

**GO for business availability.** The released software, its identity, its backup/restore posture and a live, database-connected deployment are all verified. The single open item is the *authenticated* end-to-end business-flow demonstration, which is blocked only by credential access — not by a defect.

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
| Source ↔ candidate | `evidence/commit-candidate-equivalence.json` — 1,371/1,371 exact, 0 mismatch |
| Build | Release build 0 warnings / 0 errors, from **both** the original and the renamed directory |
| Tests | CRM 255/255, API 107/107 (re-run from the renamed directory) |
| Frontend | `vue-tsc -b` + Vite production build PASS |
| Credential scan | `tools/dev/baseline-sensitive-scan.py` — 1,361 files, 16 candidates, 0 real secrets |
| Backup | `BACKUP_VERIFICATION_REPORT.md` §1–§5 |
| Restore | `VERIFY-restore.json` — pg_restore exit 0, 68/68 tables, 73/73 rows, `NASWritePerformed=false` |
| Deployment | `verify-deploy-surface.ps1` — 11/11 PASS |
| Database | 68 base tables, 73 rows, 31 CRM migrations, 1 tenant / 1 company / 1 user / 7 roles |

---

## 5. CRM smoke test — status and exactly what remains

**Verified so far (unauthenticated):** the deployed instance is healthy, database-connected, serving the release SPA, and enforcing the authentication boundary on every CRM endpoint. See §2.1.

**Not yet verified:** the authenticated chain `login → customer → follow-up → lead → confirmed demand assessment → opportunity → persistence read-back`.

The blocker is **credential access, not a defect**. The formal account `wangchunqing` (tenant `GULIXINXI`, company `GULIXINXI`, id 34) was created through the formal bootstrap with a password the operator entered interactively on this machine. It is not in Windows Credential Manager, not in `.env.local`, and not in any report — by design. **I did not reset it**, because silently replacing a business owner's credential is not an acceptable unilateral action.

The ready-to-run harness is `D:\guli\platform-deploy\v1.0\smoke-test-crm.ps1`. It takes the password from Windows Credential Manager (never from the command line, never printed), drives all seven steps, records every created record under a unique marker so the rows are identifiable and removable, and writes `logs\smoke-result.csv`. The three ways to run it are listed in §8.

---

## 6. Remaining P2 / P3 issues (none blocks the release)

| ID | Severity | Issue | Next action |
|---|---|---|---|
| R-01 | P2 | The authenticated CRM business flow is not yet demonstrated (§5) | Run `smoke-test-crm.ps1` with a credential — see §8 |
| R-02 | P2 | Third-party data sources (Quake, 站长之家) are not authorised against real supplier accounts | Keep the switches **off** for the first release; obtain supplier credentials, then re-enable behind the per-source authorisation model |
| R-03 | P2 | `docs/governance/DATABASE_TARGET_REGISTRY.md` §8 is stale — it still says `gulierp_next_prod` is "not created" and the gate is BLOCKED | Rewrite §8 to the verified reality (database exists, migrated, backup job running) |
| R-04 | P2 | The R243 IIS recovery rehearsal has never been run by an administrator (`-Execute` is refused at the entry) | Run it as administrator on the free port 8184; the failure-cleanup path is still unproven |
| R-05 | P2 | Two pre-existing IIS sites (8090 / 8091) serve an older build and their intended role is undocumented | Decide the canonical IIS target, then retire or repoint the other |
| R-06 | P2 | No CI/CD pipeline; releases are manual | `tools/dev/release-v1-backup.ps1` and the verification scripts are the seed for one |
| R-07 | P2 | The application connects as `gulidata`, which owns the CRM number sequences | Introduce the low-privilege runtime role the registry §8 planned and verify `USAGE` on `crm.company_no_seq` / `crm.deal_no_seq` |
| R-08 | P3 | 3 whitespace nits reported by `git diff --check` (2 generated EF designer files, 1 trailing space) | Left byte-identical to the frozen candidate on purpose; clean up in a dedicated hygiene commit |
| R-09 | P3 | The root file `gulierp-next` (16 KB, a captured `git diff`) is tracked in Git | Delete only with explicit approval |
| R-10 | P3 | `outputs/` holds 6 agent scratch reports; `appsettings.Development.json` and two probe scripts are untracked | Promoted into `docs/verification/` and cleared in the next iteration; all preserved in the release backup |
| R-11 | P3 | `.stack-pids.json` still records the pre-rename repository root | Regenerated automatically by `tools/dev/start-stack.ps1` |

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

1. **Demonstrate the authenticated CRM flow** (R-01) — run `smoke-test-crm.ps1`; the harness is written and waiting for a credential.
2. **Correct `DATABASE_TARGET_REGISTRY.md` §8** (R-03) — governance must match the running reality.
3. **Promote the durable CRM reports out of `outputs/`** and clear the scratch directory (R-10).
4. **Run the three-role permission matrix against the live deployment** — `CRM_OPERATOR`, `CRM_IDENTITY_REVIEWER`, `CRM_READONLY` — and record which company/tenant combinations are covered.
5. **Decide the canonical IIS target** and run the locked recovery rehearsal as administrator (R-04, R-05).
6. **Introduce the low-privilege runtime database role** (R-07), then the first real customer record, and only then consider real business data.
7. **Authorise the third-party data sources** (R-02) behind their per-source authorisation model.
8. **Repository hygiene pass** with explicit approval for deletions (R-08, R-09, R-11).

---

## 9. Where everything is

| Item | Path |
|---|---|
| Repository (canonical) | `D:\guli\projects\guli-platform` |
| GitHub | https://github.com/gulixinxi/guli-platform |
| Deployment | `D:\guli\platform-deploy\v1.0` (+ `deploy-manifest.csv`, `logs\`, `start-/stop-guli-platform.ps1`, `verify-deploy-surface.ps1`, `smoke-test-crm.ps1`) |
| Release backup | `D:\backup\guli-platform\guli-platform-v1-20261105T024244Z` (1,849 files under SHA-256) |
| Formal database backups | `D:\backup\gulierp_next_prod` (daily + weekly, last run 2026-11-05 02:10, result 0) |
| Release documents | `docs/release/RELEASE_BASELINE_REPORT.md`, `docs/release/BACKUP_VERIFICATION_REPORT.md`, `docs/release/WIP_TASK_REGISTER.md`, `docs/release/GULI_PLATFORM_V1_RELEASE_HANDOFF.md` |
| Release tooling | `tools/dev/release-v1-backup.ps1`, `tools/dev/release-v1-restore-verify.ps1`, `tools/dev/verify-commit-matches-candidate.ps1`, `tools/dev/compare-candidate-scope.ps1`, `tools/dev/group-release-scope.ps1`, `tools/dev/review-secret-candidates.ps1` |
