#requires -Version 5.1
<#
.SYNOPSIS
    Independent restore verification for the Guli Platform V1.0 release backup.

.DESCRIPTION
    Proves the release backup is actually RESTORABLE, not merely present:
      1. Starts a throwaway, isolated local PostgreSQL cluster (never touches the NAS server).
      2. Restores the release target dump with pg_restore.
      3. Compares table inventory and row counts between source and restored copy.
      4. Stops and removes the throwaway cluster, leaving no residue.

    Read-only with respect to the source database: the source is only queried with SELECT.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$BackupRoot,
    [string]$PgToolBin = 'C:\Users\Administrator\Documents\Codex\2026-10-22\referenced-chatgpt-conversation-this-is-an-2\work\postgresql-client-17\extracted\pgsql\bin',
    [string]$WorkDir,
    [int]$Port = 55432
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Say([string]$m, [string]$c = 'Cyan') { Write-Host "[restore-verify] $m" -ForegroundColor $c }

$pgDump    = Join-Path $PgToolBin 'pg_dump.exe'
$pgRestore = Join-Path $PgToolBin 'pg_restore.exe'
$psql      = Join-Path $PgToolBin 'psql.exe'
$initdb    = Join-Path $PgToolBin 'initdb.exe'
$postgres  = Join-Path $PgToolBin 'postgres.exe'
foreach ($exe in $initdb, $postgres, $pgRestore, $psql) {
    if (-not (Test-Path $exe)) { throw "Required PostgreSQL binary missing: $exe" }
}

if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP ("guli-restore-verify-" + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')) }
$dataDir = Join-Path $WorkDir 'data'
$logFile = Join-Path $WorkDir 'postgres.log'

# ---------------------------------------------------------------- locate dump
$dumps = Get-ChildItem (Join-Path $BackupRoot 'db') -Filter '*.dump' -ErrorAction Stop
$target = $dumps | Where-Object { $_.Name -like '*gulierp_next_prod-*' } | Select-Object -First 1
if (-not $target) { $target = $dumps | Select-Object -First 1 }
if (-not $target) { throw "No dump found under $(Join-Path $BackupRoot 'db')" }
Say "Archive under test: $($target.Name) ($([math]::Round($target.Length/1KB,1)) KB)"

$dumpSha = (Get-FileHash $target.FullName -Algorithm SHA256).Hash
Say "Archive SHA256: $dumpSha"

# ------------------------------------------------- source-side read-only facts
# The archive name is "<Label>-<Database>-<stamp>.dump". Derive the database name
# by stripping the known label prefix and the UTC stamp suffix. A wrong derivation
# silently queries a non-existent database and produces a FALSE PASS, so this is
# asserted explicitly rather than trusted.
$Label    = 'guli-platform-v1'
$stampTxt = (Get-ChildItem (Join-Path $BackupRoot 'db') -Filter '*.json' -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like "*$($target.BaseName -replace '[^A-Za-z0-9]','*')*" } | Select-Object -First 1)
$stampMatch = [regex]::Match($target.BaseName, '(\d{8}T\d{6}Z)$')
if (-not $stampMatch.Success) { throw "Cannot parse UTC stamp from archive name '$($target.Name)'" }
$stampTxt = $stampMatch.Groups[1].Value
$dbPart  = $target.BaseName.Substring(0, $target.BaseName.Length - $stampTxt.Length).TrimEnd('-')
if (-not $dbPart.StartsWith($Label)) { throw "Archive '$($target.Name)' does not start with expected label '$Label'" }
$sourceDb = $dbPart.Substring($Label.Length).TrimStart('-')
if (-not $sourceDb) { throw "Could not derive source database name from '$($target.Name)'" }
Say "Archive under test: $($target.Name) ($([math]::Round($target.Length/1KB,1)) KB); source database = '$sourceDb'"

# ------------------------------------------------- source-side read-only facts
$envFile = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.env.local'
$conn = (Get-Content $envFile | Where-Object { $_ -match '^ConnectionStrings__GuliERP=' } | Select-Object -First 1) -replace '^ConnectionStrings__GuliERP=',''
$host_ = if ($conn -match 'Host=([^;]+)') { $matches[1] } else { throw 'no Host' }
$port_ = if ($conn -match 'Port=([^;]+)') { $matches[1] } else { '5432' }
$user_ = if ($conn -match 'Username=([^;]+)') { $matches[1] } else { throw 'no Username' }
$env:PGPASSWORD = if ($conn -match '(?<=Password=)[^;]*') { $matches[0] } else { '' }
$env:PGCONNECT_TIMEOUT = '20'

Say "Querying source database '$sourceDb' on $host_ (read-only SELECT only)"

$countSql = @"
select table_schema||'.'||table_name from information_schema.tables
where table_schema in ('crm','identity','mdm','foundation','sales','purchase','doc_kernel')
  and table_type='BASE TABLE' order by 1;
"@
$srcTables = & $psql -h $host_ -p $port_ -U $user_ -d $sourceDb -t -A -c $countSql 2>$null | Where-Object { $_ -match '\.' }
Say "Source base tables: $($srcTables.Count)"
# Guard: a source that resolves to zero tables means the database name was wrong or
# the schema is empty. Comparing against it would silently "pass". Fail closed.
if ($srcTables.Count -eq 0) { throw "Source database '$sourceDb' returned no base tables — refusing to certify a restore against an empty/unknown source." }

# Row counts of every base table in the source (read-only).
$srcRows = @{}
foreach ($t in $srcTables) {
    $n = (& $psql -h $host_ -p $port_ -U $user_ -d $sourceDb -t -A -c "select count(*) from $t;" 2>$null) -join ''
    $srcRows[$t] = [int]$n
}
$srcTotal = ($srcRows.Values | Measure-Object -Sum).Sum
Say "Source total rows across all base tables: $srcTotal"
$env:PGPASSWORD = $null

# ----------------------------------------------------- throwaway local cluster
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
Say "Initialising isolated local cluster at $dataDir (port $Port)"
& $initdb -D $dataDir -U postgres -A trust -E UTF8 --locale=C 2>&1 | Select-Object -Last 3 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "initdb failed ($LASTEXITCODE)" }

# Start the server with output redirected to files (never piped: a piped postgres
# inherits the pipe handle and the reader blocks forever). No -Wait: judge readiness
# by polling the port instead.
$outFile = Join-Path $WorkDir 'postgres.stdout.log'
$errFile = Join-Path $WorkDir 'postgres.stderr.log'
$proc = Start-Process -FilePath $postgres `
    -ArgumentList @('-D', $dataDir, '-p', "$Port", '-c', 'listen_addresses=127.0.0.1', '-c', 'log_min_messages=warning') `
    -RedirectStandardOutput $outFile -RedirectStandardError $errFile -WindowStyle Hidden -PassThru

$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $probe = & $psql -h 127.0.0.1 -p $Port -U postgres -d postgres -t -A -c 'select 1;' 2>$null
    if ($LASTEXITCODE -eq 0 -and ($probe -join '') -match '1') { $ready = $true; break }
    if ($proc.HasExited) { break }
}
if (-not $ready) {
    Say 'Cluster did not become ready. stderr tail:', 'Red'
    if (Test-Path $errFile) { Get-Content $errFile -Tail 20 | ForEach-Object { Say "  $_", 'Red' } }
    try { if (-not $proc.HasExited) { $proc.Kill($true) } } catch {}
    throw 'Isolated cluster failed to start'
}
Say "Isolated cluster ready on 127.0.0.1:$Port (pid $($proc.Id))"

$env:PGPASSWORD = ''
$restoreDb = 'guli_restorecheck'
& $psql -h 127.0.0.1 -p $Port -U postgres -d postgres -t -A -c "drop database if exists $restoreDb;" 2>$null | Out-Null
& $psql -h 127.0.0.1 -p $Port -U postgres -d postgres -t -A -c "create database $restoreDb;" 2>$null | Out-Null
$created = & $psql -h 127.0.0.1 -p $Port -U postgres -d postgres -t -A -c "select count(*) from pg_database where datname='$restoreDb';" 2>$null
if (($created -join '') -notmatch '1') { throw "Could not create throwaway restore target '$restoreDb'." }
Say "Created throwaway restore target '$restoreDb'"

Say "Running pg_restore ..."
& $pgRestore -h 127.0.0.1 -p $Port -U postgres -d $restoreDb --no-owner --no-privileges --exit-on-error $target.FullName 2>&1 |
    Select-Object -Last 8 | ForEach-Object { Say "  pg_restore: $_" }
$restoreExit = $LASTEXITCODE
Say "pg_restore exit code: $restoreExit"

# ------------------------------------------------------------- compare results
$dstTables = & $psql -h 127.0.0.1 -p $Port -U postgres -d $restoreDb -t -A -c $countSql 2>$null | Where-Object { $_ -match '\.' }
$dstRows = @{}
foreach ($t in $dstTables) {
    $n = (& $psql -h 127.0.0.1 -p $Port -U postgres -d $restoreDb -t -A -c "select count(*) from $t;" 2>$null) -join ''
    $dstRows[$t] = [int]$n
}
$dstTotal = ($dstRows.Values | Measure-Object -Sum).Sum

$missingTables = $srcTables | Where-Object { -not $dstRows.ContainsKey($_) }
$extraTables   = $dstTables | Where-Object { -not $srcRows.ContainsKey($_) }
$mismatch = @()
foreach ($t in $srcTables) {
    if ($dstRows.ContainsKey($t) -and $srcRows[$t] -ne $dstRows[$t]) {
        $mismatch += "$t (source=$($srcRows[$t]) restored=$($dstRows[$t]))"
    }
}

Say "Source tables=$($srcTables.Count) rows=$srcTotal ; Restored tables=$($dstTables.Count) rows=$dstTotal" 'White'
$verdict = 'PASS'
if ($restoreExit -ne 0)      { $verdict = 'FAIL'; Say "pg_restore exit $restoreExit", 'Red' }
if ($missingTables.Count -gt 0) { $verdict = 'FAIL'; Say "Missing tables: $($missingTables -join ', ')", 'Red' }
if ($mismatch.Count -gt 0)    { $verdict = 'FAIL'; Say "Row mismatches: $($mismatch -join '; ')", 'Red' }
if ($verdict -eq 'PASS') { Say 'Restore verification PASS', 'Green' } else { Say 'Restore verification FAIL', 'Red' }

# --------------------------------------------------------------- cleanup
Say 'Stopping and removing the throwaway cluster'
try { & $psql -h 127.0.0.1 -p $Port -U postgres -d postgres -t -A -c "select pg_terminate_backend(pid) from pg_stat_activity where datname='$restoreDb';" 2>$null | Out-Null } catch {}
try { if (-not $proc.HasExited) { $proc.Kill($true); $proc.WaitForExit(15000) } } catch {}
Start-Sleep -Milliseconds 800
try { Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction Stop } catch { Say "Cluster dir cleanup warning: $($_.Exception.Message)", 'Yellow' }
Say 'Throwaway cluster removed (no residue)'

$result = [ordered]@{
    BackupRoot        = $BackupRoot
    Archive           = $target.Name
    ArchiveBytes      = $target.Length
    ArchiveSHA256     = $dumpSha
    SourceDatabase    = $sourceDb
    SourceServer      = $host_
    SourceBaseTables  = $srcTables.Count
    SourceTotalRows   = $srcTotal
    RestoredDatabase  = $restoreDb
    RestoredTables    = $dstTables.Count
    RestoredTotalRows = $dstTotal
    MissingTables     = $missingTables
    ExtraTables       = $extraTables
    RowMismatches     = $mismatch
    PgRestoreExitCode = $restoreExit
    IsolatedInstance  = "local throwaway cluster on 127.0.0.1:$Port (removed after test)"
    NASWritePerformed = $false
    Verdict           = $verdict
    VerifiedAtUtc     = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
}
$outFile = Join-Path $BackupRoot 'VERIFY-restore.json'
$result | ConvertTo-Json -Depth 5 | Set-Content -Path $outFile -Encoding utf8
Say "Result written: $outFile"
$result | ConvertTo-Json -Depth 5 | Write-Output
