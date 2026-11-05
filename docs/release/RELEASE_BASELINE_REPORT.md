# Guli Platform V1.0 — Release Baseline Report

**Project identity**: `gulierp-next` → `guli-platform` (product name 谷粒信息平台)
**Baseline date (UTC)**: 2026-11-05T02:42Z … T03:00Z
**Baseline window**: `D:\guli\projects\gulierp-next` → `D:\guli\projects\guli-platform`
**Remote**: `https://github.com/gulixinxi/gulierp-next.git` → `https://github.com/gulixinxi/guli-platform.git`
**Executor**: MiniMax (primary). Independent verification: Codex release-candidate pack R242b (pre-existing evidence, read-only).

---

## 1. Starting state (captured before any change)

| Item | Value |
|---|---|
| Repository | `D:\guli\projects\gulierp-next` |
| Branch | `master` |
| HEAD at baseline | `ca4fc3c62307c17ef5da5c3f3d693b74ec27a313` — *"docs: clarify CRM operator scope in role dialog"* |
| Remote at baseline | `origin https://github.com/gulixinxi/gulierp-next.git` (fetch + push) |
| Working tree | **210 changed paths** (`git status --porcelain`, directory-collapsed) = 67 modified tracked + 143 untracked entries; `git status --porcelain=v1 -uall` = 229 individual paths |
| Staged | 0 |
| Deleted | 0 |
| Local branches | 5 (`master`, `backup/pre-merge-20261019`, `backup/pre-safe-publish-20261020`, `integration/crm-merge-20261019`, `integration/crm-sanitized-20261019`) |
| Stashes | 0 |
| Tags | 0 |
| Reflog entries | 340 |

**Nothing was reset, cleaned, deleted or overwritten.** No destructive Git command was executed at any point in this release.

### 1.1 Ignored-but-materially-important assets (not in Git, therefore not in `git status`)

| Path | Size | Disposition |
|---|---:|---|
| `.env.local` | 743 B | **Contains the live database password.** Copied into the backup under `ignored/env.local`, marked restricted, and **never** committed. |
| `deploy/gulicrm-clean-deploy.dump` | 137 KB | PG 16.2 custom-format dump of database `gulicrm`. Copied into the backup. |
| `tests/_evidence_trx/` | ~8.0 MB | Operator evidence packs. Copied into the backup. |
| `docs/governance/extracted/` | 372 KB | Extracted governance source material. Copied into the backup. |
| `tools/discovery/`, `tools/.quarantine/`, `tools/dev/.quarantine`, `tools/dev/.b36c-orphan-quarantine` | ~15 MB | Discovery tooling and quarantined diagnostics. Copied into the backup. |
| `artifacts/` | 330 MB | Build output only — regenerable, deliberately not copied. |

---

## 2. Release scope — how it was determined

The release scope was **not** guessed. It was resolved against the independently frozen release candidate that already existed on this machine:

- Candidate pack: `work/release-candidate-r242b/r242-source-manifest.csv` — **1,371 files**, each with a SHA-256, produced by Codex on 2026-11-05 07:12 UTC.
- Comparison of the manifest against the live working tree (`tools/dev/compare-candidate-scope.ps1`): **1,371 / 1,371 files present, 0 SHA-256 drift.**
- Therefore the working tree *was* the release candidate; the release commit had to reproduce it exactly.

**Scope composition** (from `tools/dev/group-release-scope.ps1`):

| Area | Modified | New | Release-required |
|---|---:|---:|---|
| CRM domain / application / infrastructure (incl. EF migrations) | 20 | 96 | yes |
| Web — CRM pages | 11 | 6 | yes |
| Web — identity/org + login pages | 2 | 0 | yes |
| Web — shared api / types / utils / layout / router / components | 6 | 2 | yes |
| Web — app shell + dependency manifest (`index.html`, `package.json`, `package-lock.json`) | 3 | 0 | yes |
| API host — CRM + organization endpoints, guards, middleware, config | 5 | 6 | yes |
| Identity — CRM role packs, organization admin, seed | 4 | 0 | yes |
| Foundation — shared error codes | 1 | 0 | yes |
| Tests — CRM | 3 | 29 | yes |
| Tests — other suites | 5 | 12 | yes |
| Tools / release-ops scripts | 1 | 8 | yes (release tooling) |
| Docs & governance | 2 | 2 | yes |
| **Other ERP pages touched by the candidate** | 1 (`SalesOrderEdit.vue`) | 1 | yes — see note |
| **Agent scratch reports (`outputs/`)** | — | 6 | **no** — excluded |
| **Dev-only config** | — | 1 | **no** — excluded |
| One-off diagnostic probes | — | 2 | **no** — excluded |

> ERP-side note: `apps/web/src/views/sales-order/SalesOrderEdit.vue` (1 line) and `apps/web/src/components/transaction/LineGrid.vue` (UoM status normalisation, 7 lines) are ERP-surface edits, but they are **part of the frozen candidate** and the deployed artifact. Excluding them would have made HEAD unable to rebuild the released binary, so they are carried in the release and listed in the WIP register.

---

## 3. Verification evidence (all re-run in this release, not inherited)

| Check | Command | Result |
|---|---|---|
| Backend build | `dotnet build GuliERP.slnx -c Release` | **0 warnings, 0 errors**, 38 projects, 18.7 s |
| CRM test suite | `dotnet test tests/GuliERP.Crm.Tests -c Release --no-build` | **255 / 255 passed** |
| API test suite | `dotnet test tests/GuliERP.Api.Tests -c Release --no-build` | **107 / 107 passed** |
| Frontend build | `npm run build` (includes `vue-tsc -b`) | **PASS**, built in 19.12 s (only the pre-existing >900 kB chunk advisory) |
| Whitespace gate | `git diff --cached --check` | 3 findings — see §6 |
| Credential gate | `tools/dev/baseline-sensitive-scan.py` | 1,361 files scanned, 16 candidates, **all 16 adjudicated, 0 real secrets** |
| Candidate equivalence | `tools/dev/verify-commit-matches-candidate.ps1` | **1,371 / 1,371 byte-identical to the frozen candidate, 0 content mismatch** |

### 3.1 Credential adjudication (16 candidates)

Every candidate was reviewed in context with values masked. Classes found: runtime-generated password/token values (`AuthEndpoints.cs`, `WindowsDatabaseCredentialSource.cs`), empty-string Vue `ref('')` initialisers (`UserMenu.vue`), a boolean default and a **claim-type constant** (`MustChangePassword = "gulierp:must_change_password"`), and documentation text. **No credential material is present in the committed tree.**

---

## 4. Git history produced by this release

| Commit | Subject |
|---|---|
| `527eb08` | `release(v1.0): CRM prospecting pipeline R242 candidate + guli-platform release baseline` — 228 files, +59,475 / −688 |
| `6f2c90f` | `chore(v1.0): make repository paths rename-proof for the guli-platform directory move` — 11 files |
| `90f8737` | `chore(v1.0): rename web package identity to @guli-platform/web` — 3 files |

Push result: `527eb08..90f8737  master -> master` against `origin`.

---

## 5. Database baseline

**Engine**: PostgreSQL 17.10 (server-reported), running as a Linux/musl (Alpine) container on the NAS.
**Connection endpoint**: `192.168.2.228:5432`; server-reported address `172.29.32.2/32`.
**Release target database**: `gulierp_next_prod` (server-confirmed identity: `current_database()=gulierp_next_prod`, `current_user=gulidata`).

| Schema | Base tables |
|---|---:|
| `crm` | 32 |
| `identity` | 14 |
| `mdm` | 17 |
| `sales`, `purchase`, `doc_kernel`, `foundation` | 6 |
| **Total base tables** | **68** |
| **Total rows across all base tables** | **73** |

CRM migration history: **31 migrations**, latest `20261027092759_AddGlobalCrmNumberSequences` — matching the release candidate's migration set.
Registered state: 1 tenant, 1 company, 1 user, 7 roles, 1 company membership. **CRM business rows = 0** (clean first-use slate).

### 5.1 Protected / hold state observed (read-only, never written)

| Database | Status | Handling |
|---|---|---|
| `gulierp_g2_003_test` | **ACTIVE-USE HOLD** (user instruction 2026-10-25) | Read-only inspection only. Not written, not dumped, not migrated. |
| `gulierp_crm_p0_test_20261022` | Dev/test DB | Backed up read-only as secondary; **not** used as the formal target (registry §8 forbids it). |
| `gulierp_next_prod` | Formal release target | Backed up and used. |

**No database was created, renamed, reset, migrated or dropped by this release.**

### 5.2 Registry drift found (important)

`docs/governance/DATABASE_TARGET_REGISTRY.md` §8 still states that `gulierp_next_prod` is *"not created"* and that the release gate is *BLOCKED*. **That is stale.** The database exists, is migrated, and has a working formal backup job (`GuliERP-Next-Prod-CRM-Daily-Backup` / `-Weekly-Backup`, last run 2026-11-05 02:10, result 0, writing to `D:\backup\gulierp_next_prod`). The registry must be corrected in the next iteration — recorded in the WIP register.

---

## 6. Known non-blocking items carried into the release

| # | Item | Severity | Disposition |
|---|---|---|---|
| 1 | `git diff --check`: trailing whitespace `QuakeRuntimeCredentialStore.cs:54`; blank line at EOF in 2 generated EF `.Designer.cs` files | P3 | Left **byte-identical to the frozen candidate on purpose** — editing them would break the candidate-equivalence guarantee for no functional gain. |
| 2 | `outputs/` (6 agent reports) remain untracked | P3 | Deliberate; preserved in the backup, listed in the WIP register. |
| 3 | `apps/api/GuliERP.Api/appsettings.Development.json` remains untracked | P3 | Dev-only config pointing at the protected `gulierp_g2_003_test`; deliberately not released. |
| 4 | Root file `gulierp-next` (16 KB, a captured `git diff` dump) is tracked in Git | P3 | Pre-existing repository debt. **Not deleted** — destructive cleanup requires explicit approval. |
| 5 | Third-party data-source switches (Quake / 站长之家) are not authorised against real supplier accounts | P2 | Keep disabled for first release; documented in the handoff. |

---

## 7. Environment baseline

| Component | Version / state |
|---|---|
| .NET SDK | 10.0.403 |
| Node.js / npm | v22.22.2 / 10.9.7 |
| Git | 2.53.0.windows.2 |
| Git credential manager | Windows Credential Manager holds `git:https://github.com` for `gulixinxi` (PAT, `admin=true` on the repo) |
| PostgreSQL client | pg_dump/psql/pg_restore 17.11 (reused from the existing Codex tool cache — no new tool installed) |
| PowerShell | 7.6.0 (pwsh) |
| IIS | W3SVC running; two pre-existing sites on 8090 / 8091 already serving an older "Guli Platform" build |
| Elevation | **This session runs unelevated** — IIS site / app-pool management is not available from it (`appcmd` returns *insufficient permissions*) |
| Port 8184 | Free (reserved for the isolated IIS recovery rehearsal) |

---

## 8. Baseline conclusion

The source baseline is **sound and fully accounted for**: the working tree is byte-for-byte the independently frozen release candidate, it builds clean, its three automated suites pass, its credential surface is adjudicated, and the formal release database exists, is migrated and is covered by an operating backup job. The release may proceed on the evidence recorded here.
