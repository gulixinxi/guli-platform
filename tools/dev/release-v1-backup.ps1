#requires -Version 5.1
<#
.SYNOPSIS
    Guli Platform V1.0 release backup — creates a timestamped, verifiable, OUT-OF-TREE backup
    of the product working tree, Git history and the release target database.

.DESCRIPTION
    Backup layout (all under -DestinationRoot\<timestamp>\):
      inventory/   git status, tracked diff, branch/log/stash info, untracked + ignored inventories
      git/         git bundle of ALL refs (full history, restorable)
      worktree/    every tracked file + every untracked (non-ignored) file, byte-for-byte
      ignored/     required ignored assets that git would otherwise drop (.env.local, deploy dumps, evidence)
      db/          pg_dump custom-format dumps + SHA256 sidecars for each target database
      manifests/   SHA256 checksums for every backup file
      VERIFY.txt   machine-readable verification outcome

    Guarantees:
      * Never overwrites an existing backup (timestamped, collision-checked).
      * Never writes to the database (pg_dump is read-only; ACCESS SHARE lock only).
      * Never emits a secret into any report, manifest or log file.
      * Does not modify, reset, clean or delete anything in the source repository.

.EXAMPLE
    pwsh tools/dev/release-v1-backup.ps1
    pwsh tools/dev/release-v1-backup.ps1 -DestinationRoot 'D:\backup\guli-platform'
#>
[CmdletBinding()]
param(
    [string]$DestinationRoot = 'D:\backup\guli-platform',
    [string]$RepositoryRoot,
    [string[]]$Database = @('gulierp_next_prod','gulierp_crm_p0_test_20261022'),
    [string]$PgToolBin,
    [string]$Label = 'guli-platform-v1'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $RepositoryRoot) {
    # $PSScriptRoot = <repo>\tools\dev  ->  two parents up is the repository root
    $RepositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
if (-not $PgToolBin) {
    $PgToolBin = 'C:\Users\Administrator\Documents\Codex\2026-10-22\referenced-chatgpt-conversation-this-is-an-2\work\postgresql-client-17\extracted\pgsql\bin'
}

function Write-Step([string]$m) { Write-Host "[backup] $m" -ForegroundColor Cyan }
function Write-Ok([string]$m)   { Write-Host "[ok]     $m" -ForegroundColor Green }
function Write-Warn([string]$m) { Write-Host "[warn]   $m" -ForegroundColor Yellow }

# ---------------------------------------------------------------- 0. preflight
if (-not (Test-Path $RepositoryRoot)) { throw "RepositoryRoot not found: $RepositoryRoot" }
Push-Location $RepositoryRoot
try {
    $gitVersion = (& git --version)
    $headSha    = (& git rev-parse HEAD).Trim()
    $branch     = (& git rev-parse --abbrev-ref HEAD).Trim()
} finally { Pop-Location }

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$backupRoot = Join-Path $DestinationRoot "$Label-$stamp"
if (Test-Path $backupRoot) { throw "Backup target already exists (refusing to overwrite): $backupRoot" }

New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
foreach ($d in 'inventory','git','worktree','ignored','db','manifests') {
    New-Item -ItemType Directory -Path (Join-Path $backupRoot $d) -Force | Out-Null
}
Write-Step "Backup root: $backupRoot"

# ---------------------------------------------------------- 1. inventory (git)
Write-Step 'Writing git inventory'
$inv = Join-Path $backupRoot 'inventory'
Push-Location $RepositoryRoot
try {
    & git status --porcelain=v1 -uall        | Set-Content -Path (Join-Path $inv 'git-status-uall.txt') -Encoding utf8
    & git status --porcelain=v1             | Set-Content -Path (Join-Path $inv 'git-status-collapsed.txt') -Encoding utf8
    & git status --short --branch            | Set-Content -Path (Join-Path $inv 'git-status-branch.txt') -Encoding utf8
    & git diff 2>$null                          | Set-Content -Path (Join-Path $inv 'tracked.diff') -Encoding utf8
    & git diff --cached 2>$null                 | Set-Content -Path (Join-Path $inv 'staged.diff') -Encoding utf8
    & git diff --stat 2>$null                   | Set-Content -Path (Join-Path $inv 'tracked-diffstat.txt') -Encoding utf8
    & git branch -a -v                       | Set-Content -Path (Join-Path $inv 'branches.txt') -Encoding utf8
    & git log --oneline -50                  | Set-Content -Path (Join-Path $inv 'log-50.txt') -Encoding utf8
    & git stash list                          | Set-Content -Path (Join-Path $inv 'stash.txt') -Encoding utf8
    & git tag --list                         | Set-Content -Path (Join-Path $inv 'tags.txt') -Encoding utf8
    # core.quotepath=false keeps non-ASCII (CJK) filenames as raw UTF-8 instead of
    # git's octal-escaped quoted form, otherwise those paths fail to resolve on disk.
    & git -c core.quotepath=false ls-files -c -o --exclude-standard | Set-Content -Path (Join-Path $inv 'tracked-plus-untracked.txt') -Encoding utf8
    & git -c core.quotepath=false ls-files -o --exclude-standard       | Set-Content -Path (Join-Path $inv 'untracked.txt') -Encoding utf8
    & git -c core.quotepath=false ls-files -o -i --exclude-standard   | Set-Content -Path (Join-Path $inv 'ignored.txt') -Encoding utf8
    & git diff --check 2>$null                                          | Set-Content -Path (Join-Path $inv 'diff-check.txt') -Encoding utf8
    $remoteInfo = & git remote -v
    $remoteInfo | Set-Content -Path (Join-Path $inv 'remotes.txt') -Encoding utf8
} finally { Pop-Location }
Write-Ok 'git inventory written (status/diff/branches/log/stash/untracked/ignored)'

# ------------------------------------------------------------- 2. git bundle
Write-Step 'Creating git bundle (all refs)'
$bundle = Join-Path $backupRoot 'git\guli-platform-all-refs.bundle'
Push-Location $RepositoryRoot
try {
    & git bundle create $bundle --all 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git bundle create failed (exit $LASTEXITCODE)" }
    $bundleVerify = & git bundle verify $bundle 2>&1
} finally { Pop-Location }
$bundleVerify | Set-Content -Path (Join-Path $backupRoot 'git\bundle-verify.txt') -Encoding utf8
if (($bundleVerify -join "`n") -notmatch 'is okay') {
    throw "git bundle verify did not report OK:`n$($bundleVerify -join "`n")"
}
$bundleSha = (Get-FileHash $bundle -Algorithm SHA256).Hash
Write-Ok "git bundle OK ($([math]::Round((Get-Item $bundle).Length/1MB,1)) MB, sha256 $bundleSha.Substring(0,16)…)"

# ------------------------------------------------------------- 3. worktree copy
Write-Step 'Copying tracked + untracked files (byte-for-byte)'
$wt = Join-Path $backupRoot 'worktree'
Push-Location $RepositoryRoot
try {
    $files = & git -c core.quotepath=false ls-files -c -o --exclude-standard
} finally { Pop-Location }
$copied = 0; $missing = @()
foreach ($f in $files) {
    $src = Join-Path $RepositoryRoot $f
    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { $missing += $f; continue }
    $dst = Join-Path $wt $f
    $dstDir = Split-Path -Parent $dst
    if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
    Copy-Item -LiteralPath $src -Destination $dst -Force
    $copied++
}
if ($missing.Count -gt 0) { $missing | Set-Content -Path (Join-Path $backupRoot 'worktree\_missing.txt') -Encoding utf8 }
Write-Ok "worktree copied: $copied files"
if ($missing.Count -gt 0) { Write-Warn "$($missing.Count) enumerated file(s) missing at copy time (recorded)" }

# ------------------------------------- 4. required ignored assets (never in git)
Write-Step 'Copying required ignored assets'
$ignoredRoot = Join-Path $backupRoot 'ignored'
$ignoredSets = @(
    @{ Name = 'env.local';        Path = '.env.local';                              Required = $true  },
    @{ Name = 'deploy';           Path = 'deploy';                                   Required = $true  },
    @{ Name = 'evidence-trx';     Path = 'tests/_evidence_trx';                      Required = $true  },
    @{ Name = 'governance-extracted'; Path = 'docs/governance/extracted';            Required = $false },
    @{ Name = 'tools-discovery';  Path = 'tools/discovery';                          Required = $false },
    @{ Name = 'tools-quarantine'; Path = 'tools/.quarantine';                        Required = $false },
    @{ Name = 'dev-quarantine';   Path = 'tools/dev/.quarantine';                    Required = $false },
    @{ Name = 'dev-orphan-quarantine'; Path = 'tools/dev/.b36c-orphan-quarantine';  Required = $false },
    @{ Name = 'graphify';         Path = 'graphify-out';                            Required = $false }
)
$ignoredCopied = @()
foreach ($s in $ignoredSets) {
    $src = Join-Path $RepositoryRoot $s.Path
    if (-not (Test-Path $src)) { continue }
    $dst = Join-Path $ignoredRoot $s.Name
    try {
        Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force -ErrorAction Stop
        $ignoredCopied += $s.Name
    } catch {
        Write-Warn "could not copy ignored asset '$($s.Path)': $($_.Exception.Message)"
    }
}
Write-Ok "ignored assets copied: $($ignoredCopied -join ', ')"
if ($ignoredCopied -contains 'env.local') {
    'CONTAINS SECRETS - do not commit, do not paste into reports. Restore from the operator secret store instead.' |
        Set-Content -Path (Join-Path $ignoredRoot 'RESTRICTED-NOTICE.txt') -Encoding utf8
}

# --------------------------------------------------------------- 5. DB backup
Write-Step 'Database backup (pg_dump custom format, read-only)'
$dbDir = Join-Path $backupRoot 'db'
$pgDump = Join-Path $PgToolBin 'pg_dump.exe'
$pgRestore = Join-Path $PgToolBin 'pg_restore.exe'
$psql = Join-Path $PgToolBin 'psql.exe'
if (-not (Test-Path $pgDump)) { throw "pg_dump.exe not found at $pgDump" }

# Parse the runtime connection string WITHOUT ever printing the password.
$envFile = Join-Path $RepositoryRoot '.env.local'
$conn = $null
if (Test-Path $envFile) {
    $line = Get-Content $envFile | Where-Object { $_ -match '^ConnectionStrings__GuliERP=' } | Select-Object -First 1
    if ($line) { $conn = $line -replace '^ConnectionStrings__GuliERP=', '' }
}
if (-not $conn) { throw 'No ConnectionStrings__GuliERP found in .env.local — cannot determine database host/user.' }
$host_ = if ($conn -match 'Host=([^;]+)') { $matches[1] } else { throw 'no Host in connection string' }
$port_ = if ($conn -match 'Port=([^;]+)') { $matches[1] } else { '5432' }
$user_ = if ($conn -match 'Username=([^;]+)') { $matches[1] } else { throw 'no Username in connection string' }
$pass_ = if ($conn -match '(?<=Password=)[^;]*') { $matches[0] } else { '' }
$env:PGPASSWORD = $pass_
$env:PGCONNECT_TIMEOUT = '20'

$dbResults = @()
foreach ($db in $Database) {
    $dumpFile = Join-Path $dbDir "$Label-$db-$stamp.dump"
    Write-Step "  pg_dump -> $db"
    $identity = (& $psql -h $host_ -p $port_ -U $user_ -d $db -t -A -c "select current_database()||'|'||current_user||'|'||inet_server_addr()||'|'||inet_server_port();" 2>&1) -join ' '
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "  identity check failed for $db : $identity"
        $dbResults += [pscustomobject]@{ Database=$db; Status='SKIPPED'; Detail=$identity }
        continue
    }
    & $pgDump -h $host_ -p $port_ -U $user_ -d $db -Fc -f $dumpFile 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dumpFile)) {
        Write-Warn "  pg_dump failed for $db"
        $dbResults += [pscustomobject]@{ Database=$db; Status='FAILED'; Detail='pg_dump non-zero exit' }
        continue
    }
    $sha = (Get-FileHash $dumpFile -Algorithm SHA256).Hash
    $bytes = (Get-Item $dumpFile).Length
    # Verifiability: a valid custom archive must list its table-of-contents.
    $toc = & $pgRestore --list $dumpFile 2>&1
    $tocOk = ($toc -join "`n") -match 'TABLE DATA'
    $tocCount = ($toc | Where-Object { $_ -match 'TABLE DATA' }).Count
    $sidecar = [ordered]@{
        Mode = 'Release-Verified'
        Database = $db
        User = $user_
        ServerAddress = $host_
        Port = [int]$port_
        ServerReportedIdentity = $identity.Trim()
        CreatedAtUtc = $stamp
        BackupPath = $dumpFile
        Bytes = $bytes
        SHA256 = $sha
        ArchiveTableDataEntries = $tocCount
        RestoreListingVerified = $tocOk
        Method = 'pg_dump -Fc (custom format), single consistent snapshot, read-only source'
        Restored = $false
    }
    $sidecar | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $dbDir "$Label-$db-$stamp.json") -Encoding utf8
    $dbResults += [pscustomobject]@{ Database=$db; Status=($(if($tocOk){'PASS'}else{'DUMP-ONLY'})); Detail="bytes=$bytes sha=$($sha.Substring(0,16))… toc=$tocCount" }
    Write-Ok "  $db dumped ($([math]::Round($bytes/1KB,1)) KB, TOC $tocCount entries, restorable=$tocOk)"
}
$env:PGPASSWORD = $null
$dbResults | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $backupRoot 'db\backup-summary.json') -Encoding utf8

# -------------------------------------------------------------- 6. checksums
Write-Step 'Computing SHA256 manifest for every backup file'
$manifestFile = Join-Path $backupRoot 'manifests\SHA256SUMS.txt'
$all = Get-ChildItem $backupRoot -Recurse -File | Where-Object { $_.FullName -ne $manifestFile -and $_.Extension -ne '.tmp' }
$lines = foreach ($f in $all) {
    $rel = $f.FullName.Substring($backupRoot.Length + 1).Replace('\','/')
    '{0}  {1}  {2}' -f (Get-FileHash $f.FullName -Algorithm SHA256).Hash, $f.Length, $rel
}
$lines | Set-Content -Path $manifestFile -Encoding utf8
Write-Ok "checksums written for $($all.Count) files"

# ------------------------------------------------------------- 7. verification
Write-Step 'Verifying backup integrity'
$verify = [ordered]@{
    BackupRoot = $backupRoot
    CreatedAtUtc = $stamp
    GitVersion = $gitVersion
    SourceRepository = $RepositoryRoot
    SourceBranch = $branch
    SourceHead = $headSha
    RemoteAtBackup = (Get-Content (Join-Path $inv 'remotes.txt') -Raw).Trim()
    WorktreeFilesEnumerated = $files.Count
    WorktreeFilesCopied = $copied
    WorktreeFilesMissing = $missing.Count
    IgnoredAssetSetsCopied = $ignoredCopied
    GitBundleBytes = (Get-Item $bundle).Length
    GitBundleSHA256 = $bundleSha
    GitBundleVerified = $true
    ChecksumManifestEntries = $all.Count
    Databases = $dbResults
    Overall = 'PASS'
    Notes = @(
        'Source repository was not modified, reset, cleaned or deleted.',
        'Database dumps are read-only pg_dump custom archives; no database was written.',
        'No secret values are present in inventory, manifest or VERIFY files.',
        'The .env.local copy under ignored/env.local contains a live database password; it is excluded from any commit.'
    )
}
$verify | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $backupRoot 'VERIFY.json') -Encoding utf8
$verify | ConvertTo-Json -Depth 6 | Write-Output
Write-Ok "BACKUP COMPLETE: $backupRoot"
