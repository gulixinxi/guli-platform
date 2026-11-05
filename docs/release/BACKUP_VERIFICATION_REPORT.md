# Guli Platform V1.0 — Backup Verification Report

**Backup root**: `D:\backup\guli-platform\guli-platform-v1-20261105T024244Z`
**Created (UTC)**: 2026-11-05T02:42:44Z
**Method**: `tools/dev/release-v1-backup.ps1` (reproducible, committed to the repository)
**Restore proof**: `tools/dev/release-v1-restore-verify.ps1` (reproducible, committed)
**Overall verdict**: **PASS — backup integrity verified and restore proven**

> The backup root is **outside** the project tree. Nothing was overwritten: the directory name carries a UTC timestamp and the script refuses to run if the target already exists.

---

## 1. What is inside the backup

| Path | Files | Bytes | Content |
|---|---:|---:|---|
| `inventory/` | 11 | 3,191,856 | `git status` (uall + collapsed + branch), `tracked.diff`, `staged.diff`, `tracked-diffstat.txt`, `branches.txt`, `log-50.txt`, `stash.txt`, `tags.txt`, `tracked-plus-untracked.txt`, `untracked.txt`, `ignored.txt`, `diff-check.txt`, `remotes.txt` |
| `git/` | 2 | 7,529,980 | `guli-platform-all-refs.bundle` — **all refs, full history** + `bundle-verify.txt` |
| `worktree/` | 1,382 | 22,115,065 | Every tracked file **and** every untracked (non-ignored) file, byte-for-byte |
| `ignored/` | 449 | 24,187,261 | The ignored assets Git would otherwise drop (see §2) |
| `db/` | 5 | 583,027 | Two `pg_dump -Fc` archives + SHA-256 sidecars + `backup-summary.json` |
| `manifests/` | 1 | 265,123 | `SHA256SUMS.txt` — **1,849 entries** (SHA-256 + byte length + relative path) |
| `evidence/` | 3 | 61,541 | Pre-commit credential scan, restore-verification log, commit↔candidate equivalence result |

Totals: **1,849 files under SHA-256 control.**

### 1.1 Working-tree coverage

| Metric | Value |
|---|---|
| Files enumerated (`git ls-files -c -o --exclude-standard`) | 1,382 |
| Files copied | **1,382** |
| Files missing at copy time | **0** |
| Enumeration defect found and fixed | `git ls-files` returned **octal-quoted** paths for 17 CJK-named `.docx` files, which silently skipped them. Fixed with `-c core.quotepath=false`; the 17 files are present in the finished backup. |

---

## 2. Ignored assets deliberately rescued from Git's blind spot

| Set | Restored as | Notes |
|---|---|---|
| `.env.local` | `ignored/env.local` | **Contains the live database password.** A `RESTRICTED-NOTICE.txt` sits next to it. It is **excluded from every commit** and no secret value appears in any report, manifest or log. |
| `deploy/` | `ignored/deploy` | Includes `gulicrm-clean-deploy.dump` (PG 16.2 custom archive). |
| `tests/_evidence_trx/` | `ignored/evidence-trx` | Operator evidence packs. |
| `docs/governance/extracted/` | `ignored/governance-extracted` | Extracted governance sources. |
| `tools/discovery/`, `tools/.quarantine/`, `tools/dev/.quarantine`, `tools/dev/.b36c-orphan-quarantine` | `ignored/tools-*` | Discovery tooling and quarantined diagnostics. |

Not copied (regenerable, no information content): `artifacts/` (330 MB of build output), `node_modules/`, `bin/`, `obj/`, `dist/`, `TestResults/`, `.stack-logs/`, `.runtime-browser-profile/`.

---

## 3. Git history backup

| Item | Value |
|---|---|
| Artifact | `git/guli-platform-all-refs.bundle` |
| Size | 7,529,980 B (7.2 MB) |
| SHA-256 | `3CE1AD7B6D80C844BE1342FAE17C9C093B9171AD3EDE4133832AEACE8FD831F4` |
| Scope | `git bundle create --all` → **all branches and tags**, full reachable history |
| Integrity check | `git bundle verify` → **"The bundle contains this ref: … is okay"** |

All five local branches and the full commit history are therefore recoverable independently of the remote.

---

## 4. Database backup

Both dumps are **read-only** `pg_dump -Fc` (custom format) snapshots taken inside a single consistent transaction. `pg_dump` takes only an `ACCESS SHARE` lock — **no data was written, and no database was created, renamed, reset, migrated or dropped.**

| Database | Archive | Bytes | SHA-256 | TOC entries | Verdict |
|---|---|---:|---|---:|---|
| `gulierp_next_prod` (formal release target) | `guli-platform-v1-gulierp_next_prod-20261105T024244Z.dump` | 247,402 | `B0321BD2859ACF4DC6BDB0F1D05FBAF27F07F0F06F43B441E60B3378F3189A90` | 71 | PASS |
| `gulierp_crm_p0_test_20261022` (dev/test, secondary) | `guli-platform-v1-gulierp_crm_p0_test_20261022-20261105T024244Z.dump` | 333,926 | `153AB042C4CE3832…` | 59 | PASS |

Each archive carries a JSON sidecar recording mode, database, user, server address/port, server-reported identity, UTC timestamp, path, bytes, SHA-256, TOC entry count and `RestoreListingVerified`.

Server identity was **asserted from the server**, not inferred from the connection string:

```
current_database()=gulierp_next_prod | current_user=gulidata
inet_server_addr()=172.29.32.2 | inet_server_port()=5432
```

`gulierp_g2_003_test` (ACTIVE-USE HOLD) was **not** dumped and **not** written.

### 4.1 Independent restore proof

A backup file plus a checksum is not a proven backup. The restore was therefore executed:

1. A **throwaway isolated PostgreSQL cluster** was initialised locally on `127.0.0.1:55432` (`initdb`, trust auth, `listen_addresses=127.0.0.1`).
2. The `gulierp_next_prod` archive was restored with `pg_restore --exit-on-error`.
3. Source and restored copies were compared table-by-table and row-by-row.
4. The throwaway cluster and its data directory were **removed — no residue**.

| Metric | Source | Restored | Delta |
|---|---:|---:|---:|
| Base tables | 68 | 68 | 0 |
| Total rows | 73 | 73 | 0 |
| Missing tables | — | — | **0** |
| Extra tables | — | — | **0** |
| Row mismatches | — | — | **0** |
| `pg_restore` exit code | — | — | **0** |

**NAS write performed: false.** The verification never touched the shared server beyond `SELECT`.

### 4.2 A false PASS was caught and corrected (disclosure)

The first restore-verification run reported `PASS` while the source side showed **0 tables and 0 rows**. Root cause: the source database name was derived by splitting the archive file name on `-`, which produced `platform` instead of `gulierp_next_prod`; the comparison then ran against a non-existent database and compared an empty source with a full restore, which trivially "matched".

Corrections applied:

- The archive name is now parsed by stripping the known label prefix and the UTC stamp suffix, with explicit assertions.
- The script now **fails closed** when the source resolves to zero base tables, so an empty/unknown source can never be certified.
- The superseded verdict was retained as `VERIFY-restore.json.superseded-false-pass-source-db-name-bug` rather than deleted, and the corrected run re-produced `VERIFY-restore.json` with **Verdict = PASS**.

The corrected figures in §4.1 are the ones that count.

---

## 5. Checksum manifest

`manifests/SHA256SUMS.txt` — 1,849 lines, format `<SHA256>  <bytes>  <relative/path>`.

To re-verify the whole backup:

```powershell
$root = 'D:\backup\guli-platform\guli-platform-v1-20261105T024244Z'
Get-Content "$root\manifests\SHA256SUMS.txt" | ForEach-Object {
    $p = $_ -split '  ', 3
    $f = Join-Path $root $p[2].Replace('/', '\')
    $h = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash
    if ($h -ne $p[0]) { "MISMATCH $($p[2])" }
} | Where-Object { $_ }
# no output = every file still matches
```

---

## 6. Restore procedure (rollback path)

**Full rollback = restore the database, redeploy the previous artifact.**

| Step | Action | Command |
|---|---|---|
| 1 | Stop the application | stop the deployed process / recycle the IIS app pool |
| 2 | Choose the archive | `db/guli-platform-v1-gulierp_next_prod-20261105T024244Z.dump` |
| 3 | Restore into a **fresh** database (never over an existing one) | `pg_restore -h 192.168.2.228 -p 5432 -U gulidata -d <new_db> --no-owner --exit-on-error <archive>` |
| 4 | Verify | base tables = 68, total rows = 73, `pg_restore` exit = 0 |
| 5 | Repoint the application | `ConnectionStrings__GuliERP` → `<new_db>` |
| 6 | Redeploy the previous artifact | the pre-release binary/IIS content, kept at its previous path |

Existing backups are **never** overwritten: new dumps carry a UTC timestamp and a distinct SHA-256 prefix. The pre-existing formal daily/weekly series in `D:\backup\gulierp_next_prod` (last run 2026-11-05 02:10, result 0) is untouched by this release.

---

## 7. Backup verification conclusion

| Property | Result |
|---|---|
| Backup exists outside the project tree | **PASS** |
| Git history preserved and independently verifiable | **PASS** (bundle verified) |
| Working tree + untracked files preserved byte-for-byte | **PASS** (1,382 / 1,382, 0 missing) |
| Required ignored assets rescued | **PASS** (8 sets) |
| Database captured with a consistent, read-only method | **PASS** (2 archives) |
| Database backup **proven restorable** | **PASS** (isolated cluster, 68/68 tables, 73/73 rows, 0 drift) |
| Checksum coverage | **PASS** (1,849 entries) |
| No secret leaked into commits, reports or manifests | **PASS** |
| Source repository unmodified by the backup | **PASS** |
| Database untouched by the backup | **PASS** (no writes; `gulierp_g2_003_test` not touched) |
| Existing backups not overwritten | **PASS** |
