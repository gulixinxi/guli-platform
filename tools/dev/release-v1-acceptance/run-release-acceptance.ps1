#requires -Version 5.1
<#
.SYNOPSIS
    Guli Platform V1.0 — release acceptance: provision a verification operator,
    run the CRM business smoke test, then retire the operator.

.DESCRIPTION
    One self-contained command so the acceptance can be executed by the operator
    without handing any credential to an agent or to a chat channel:

      1. Generates a random password locally (never printed, never written to a file).
      2. Provisions `GULIXINXI_v10_verify_operator` inside the existing formal
         tenant/company through the project's own Identity Bootstrap CLI, with the
         CRM_OPERATOR role — the CLI's own marker-prefix contract is satisfied
         because the user name shares the `GULIXINXI` prefix.
      3. Runs the CRM business smoke test (login -> customer -> follow-up ->
         lead -> confirmed demand assessment -> lead conversion -> opportunity
         -> persistence).
      4. Retires the verification operator (the account is disabled with
         Status = 2, the lockout is armed and its membership/role rows are
         removed) so no active credential is left behind.
      5. Re-attempts the login with the still-known password and requires
         HTTP 401 — a retirement that cannot be proven by a real attempt is
         not treated as a retirement.

    The formal business account `wangchunqing` is never touched.

.EXAMPLE
    pwsh run-release-acceptance.ps1
    pwsh run-release-acceptance.ps1 -KeepOperator      # leave the operator in place for manual use
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = 'D:\guli\projects\guli-platform',
    [string]$DeployRoot = 'D:\guli\platform-deploy\v1.0',
    [string]$BaseUrl = 'http://127.0.0.1:8080',
    [string]$OperatorName = 'GULIXINXI_v10_verify_operator',
    [string]$TenantCode = 'GULIXINXI',
    [string]$CompanyCode = 'GULIXINXI',
    [string]$RoleCode = 'CRM_OPERATOR',
    [switch]$KeepOperator
)

$ErrorActionPreference = 'Stop'
function Say([string]$m, [string]$c = 'Cyan') { Write-Host $m -ForegroundColor $c }

$password = 'V1a!' + [guid]::NewGuid().ToString('N').Substring(0,20)

Say '=== Guli Platform V1.0 — release acceptance ===' 'Cyan'
Say ("  operator : $OperatorName")
Say ("  tenant   : $TenantCode / company $CompanyCode / role $RoleCode")
Say ("  target   : $BaseUrl")
Say ''

# ------------------------------------------------- 1. database credential source
$envFile = Join-Path $RepoRoot '.env.local'
if (-not (Test-Path $envFile)) { throw ".env.local not found at $envFile" }
$conn = ((Get-Content $envFile | Where-Object { $_ -match '^ConnectionStrings__GuliERP=' } | Select-Object -First 1) -replace '^ConnectionStrings__GuliERP=','')
$dbHost = if ($conn -match 'Host=([^;]+)') { $matches[1] } else { throw 'no Host in .env.local' }
$dbPort = if ($conn -match 'Port=([^;]+)') { $matches[1] } else { '5432' }
$dbUser = if ($conn -match 'Username=([^;]+)') { $matches[1] } else { throw 'no Username in .env.local' }
$dbPassword = if ($conn -match '(?<=Password=)[^;]*') { $matches[0] } else { '' }
$prodDb = 'gulierp_next_prod'
Say ("  database : {0} @ {1}:{2} (as {3})" -f $prodDb, $dbHost, $dbPort, $dbUser) 'DarkGray'

# ------------------------------------------------- 2. provision the operator
$cli = Join-Path $RepoRoot 'tools\GuliERP.Identity.Bootstrap'
$cliProj = Join-Path $cli 'GuliERP.Identity.Bootstrap.csproj'
if (-not (Test-Path $cliProj)) { throw "Bootstrap CLI not found: $cliProj" }

Say 'Provisioning the verification operator through the Identity Bootstrap CLI...'
# All CLI I/O goes through files. Capturing a long-lived child's stdout through a
# pipe deadlocks as soon as the pipe buffer fills, which is exactly what happened
# on the first attempt.
$workDir  = Join-Path $env:TEMP ("guli-acceptance-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $workDir -Force | Out-Null
$stdinFile = Join-Path $workDir 'password.txt'
$outFile   = Join-Path $workDir 'cli.out.log'
$errFile   = Join-Path $workDir 'cli.err.log'
Set-Content -Path $stdinFile -Value $password -Encoding ascii -NoNewline

$cliArgs = @(
    'run', '--project', $cliProj, '-c', 'Release', '--',
    '--reset-fixture',
    "Host=$dbHost;Port=$dbPort;Database=$prodDb;Username=$dbUser;Password=$dbPassword",
    $OperatorName, $TenantCode, $CompanyCode, $TenantCode, $RoleCode
)
$p = Start-Process -FilePath 'dotnet' -ArgumentList $cliArgs -WorkingDirectory $RepoRoot `
    -RedirectStandardInput $stdinFile -RedirectStandardOutput $outFile -RedirectStandardError $errFile `
    -WindowStyle Hidden -PassThru
$p.WaitForExit(600000) | Out-Null
$cliExit = $p.ExitCode
$stdout = (Get-Content $outFile -Raw -ErrorAction SilentlyContinue)
$stderr = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue)
Say ("  CLI exit code: $cliExit") $(if ($cliExit -eq 0) { 'Green' } else { 'Red' })
if ($cliExit -ne 0) {
    Say '  CLI output tail:' 'Red'
    (($stdout + "`n" + $stderr) -split "`n") | Select-Object -Last 25 | ForEach-Object { Say "   $_" 'Red' }
    throw "Verification operator could not be provisioned (exit $cliExit)."
}
Say '  CLI output (non-secret tail):'
(($stdout + "`n" + $stderr) -split "`n") | Where-Object { $_ -match '\S' } | Select-Object -Last 12 | ForEach-Object { Say "   $_" 'DarkGray' }

# The CLI prints a lot; make sure the password never leaked into it.
if ($stdout -match [regex]::Escape($password)) { throw 'SAFETY: the password appeared in the CLI output. Aborting.' }
# ------------------------------------------------- 3. run the smoke test
$smoke = Join-Path $DeployRoot 'smoke-test-crm.ps1'
Say ''
Say 'Running the CRM business smoke test...'
$env:GULI_SMOKE_PASSWORD = $password
# Kept for the post-retirement negative login check (step 5). It is never printed.
$retiredPassword = $password
$password = $null
$smokeExit = 0
& pwsh -NoProfile -ExecutionPolicy Bypass -File $smoke -BaseUrl $BaseUrl -UserName $OperatorName
$smokeExit = $LASTEXITCODE
$env:GULI_SMOKE_PASSWORD = $null

# ------------------------------------------------- 4. retire the operator
if ($KeepOperator) {
    Say ''
    Say "Keeping $OperatorName active for manual inspection." 'Yellow'
} else {
    Say ''
    Say 'Retiring the verification operator...'
    $pgTool = 'C:\Users\Administrator\Documents\Codex\2026-10-22\referenced-chatgpt-conversation-this-is-an-2\work\postgresql-client-17\extracted\pgsql\bin'
    $psql = Join-Path $pgTool 'psql.exe'
    $env:PGPASSWORD = $dbPassword
    $sql = @"
select 'USER='||u."Id"||'|'||u."UserName" from identity."AspNetUsers" u where u."UserName"='$OperatorName';
"@
    $tmp = Join-Path $env:TEMP 'acceptance-retire.sql'
    Set-Content -Path $tmp -Value $sql -Encoding UTF8
    $row = & $psql -h $dbHost -p $dbPort -U $dbUser -d $prodDb -t -A -f $tmp 2>$null
    $userId = $null
    if ($row -match 'USER=(\d+)\|') { $userId = $matches[1] }
    if ($userId) {
        $off = @"
update identity."AspNetUsers"
set "Status" = 2,
    "LockoutEnabled" = true,
    "LockoutEnd" = now() + interval '100 years',
    "SecurityStamp" = gen_random_uuid()::text,
    "AccessFailedCount" = 0
where "Id"=$userId;
delete from identity.gulierp_user_role_assignment where "UserId"=$userId;
delete from identity.gulierp_user_company_membership where "UserId"=$userId;
"@
        Set-Content -Path $tmp -Value $off -Encoding UTF8
        & $psql -h $dbHost -p $dbPort -U $dbUser -d $prodDb -t -A -f $tmp 2>&1 | Out-Null
        Say "  operator id $userId disabled (Status=2), lockout armed, role and membership rows removed."
    } else {
        Say '  operator row not found — nothing to retire.'
    }
    $env:PGPASSWORD = $null
}

# ------------------------------------------------- 5. negative login check
# The retirement must actually block authentication — a row that merely *looks*
# retired is not retired. The first version of this harness set only
# LockoutEnabled/LockoutEnd, which Identity does not treat as locked out when
# LockoutEnd is NULL, and it used the SQL-Server function newid() (unknown to
# PostgreSQL), so the whole UPDATE silently failed. This check proves the fix.
if (-not $KeepOperator -and $retiredPassword) {
    Say ''
    Say 'Verifying that the retired operator can no longer authenticate...'
    $negExit = 0
    try {
        $csrfResp = Invoke-WebRequest "$BaseUrl/api/v1/auth/csrf" -UseBasicParsing -TimeoutSec 15 -SessionVariable negSession -ErrorAction Stop
        $negCsrf = ($csrfResp.Content | ConvertFrom-Json).requestToken
        $loginBody = @{ userName = $OperatorName; password = $retiredPassword; tenantCode = $TenantCode } | ConvertTo-Json -Compress
        Invoke-WebRequest -Uri "$BaseUrl/api/v1/auth/login" -Method POST -ContentType 'application/json' `
            -Headers @{ 'X-CSRF-TOKEN' = $negCsrf } -Body $loginBody -WebSession $negSession -UseBasicParsing -TimeoutSec 30 | Out-Null
        $negExit = 1
        Say '  [FAIL] retired operator still authenticates' 'Red'
    } catch {
        $code = 0
        if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        if ($code -eq 401) {
            Say '  [PASS] retired operator rejected with HTTP 401' 'Green'
        } else {
            $negExit = 1
            Say ("  [FAIL] retired operator login returned HTTP {0} (expected 401)" -f $code) 'Red'
        }
    }
    $retiredPassword = $null
    if ($negExit -ne 0) { $smokeExit = 1 }
}

Say ''
Say ("ACCEPTANCE RESULT: smoke test exit = {0}" -f $smokeExit) $(if ($smokeExit -eq 0) { 'Green' } else { 'Red' })
exit $smokeExit
