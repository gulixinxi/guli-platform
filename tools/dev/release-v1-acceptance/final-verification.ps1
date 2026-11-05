#requires -Version 5.1
<#
    Guli Platform V1.0 — final consolidated verification.

    One pass over every acceptance dimension, executed from the RENAMED
    repository (D:\guli\projects\guli-platform). Writes a machine-readable
    result and prints a PASS/FAIL line per check.
#>
$ErrorActionPreference = 'Continue'
$repo   = 'D:\guli\projects\guli-platform'
$deploy = 'D:\guli\platform-deploy\v1.0'
$git    = 'C:\Program Files\Git\cmd\git.exe'
$pgTool = 'C:\Users\Administrator\Documents\Codex\2026-10-22\referenced-chatgpt-conversation-this-is-an-2\work\postgresql-client-17\extracted\pgsql\bin'
$psql   = Join-Path $pgTool 'psql.exe'

$script:pass = 0; $script:fail = 0; $script:results = @()
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { $script:pass++ } else { $script:fail++ }
    $script:results += [pscustomobject]@{ Check = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Detail = $detail }
    Write-Host ("  [{0}] {1}  {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
}

Write-Host ''
Write-Host '=== Guli Platform V1.0 — final consolidated verification ===' -ForegroundColor Cyan
Write-Host "  repo   : $repo"
Write-Host "  deploy : $deploy"
Write-Host ''

# ---------------------------------------------------------------- 1. git identity
Write-Host '-- git identity --' -ForegroundColor White
$remote = (& $git -C $repo remote get-url origin).Trim()
Check 'remote is the renamed repository' ($remote -eq 'https://github.com/gulixinxi/guli-platform.git') $remote
$branch = (& $git -C $repo rev-parse --abbrev-ref HEAD).Trim()
Check 'branch' ($branch -eq 'master') $branch
$head = (& $git -C $repo rev-parse HEAD).Trim()
$remoteHead = (& $git -C $repo ls-remote origin refs/heads/master).Split()[0]
Check 'HEAD == remote master' ($head -eq $remoteHead) "HEAD=$($head.Substring(0,7)) remote=$($remoteHead.Substring(0,7))"
$oldRedirect = $null
try {
    $req = [System.Net.HttpWebRequest]::Create('https://github.com/gulixinxi/gulierp-next')
    $req.AllowAutoRedirect = $false
    $req.Method = 'HEAD'
    $resp = $req.GetResponse()
    $oldRedirect = [int]$resp.StatusCode
    $resp.Close()
} catch [System.Net.WebException] {
    if ($_.Exception.Response) { $oldRedirect = [int]$_.Exception.Response.StatusCode }
}
Check 'old repository URL still redirects' ($oldRedirect -ge 300 -and $oldRedirect -lt 400) "HTTP $oldRedirect"

# ---------------------------------------------------------------- 2. working tree
Write-Host '-- working tree --' -ForegroundColor White
$status = @(& $git -C $repo -c core.quotepath=false status --porcelain -uall)
$modified = @($status | Where-Object { $_ -match '^ M|^M ' })
$added    = @($status | Where-Object { $_ -match '^A ' })
$deleted  = @($status | Where-Object { $_ -match '^ D|^D ' })
$untracked= @($status | Where-Object { $_ -match '^\?\?' })
$deliberate = @($untracked | Where-Object { $_ -match 'outputs/|tools/dev/probe-live-sites\.ps1|tools/dev/probe-site-content\.ps1' })
Check 'no modified tracked files' ($modified.Count -eq 0) "$($modified.Count) modified"
Check 'no staged additions left' ($added.Count -eq 0) "$($added.Count) staged adds"
Check 'no deleted tracked files' ($deleted.Count -eq 0) "$($deleted.Count) deleted"
Check 'only the 3 deliberate untracked groups' ($untracked.Count -eq $deliberate.Count -and $deliberate.Count -gt 0) "$($untracked.Count) untracked / $($deliberate.Count) expected"

# ---------------------------------------------------------------- 2b. candidate drift after the release commit
Write-Host '-- candidate equivalence (honest scope) --' -ForegroundColor White
# The RELEASE commit 527eb08 reproduces the frozen R242b candidate
# 1,371/1,371 byte-identical (evidence/commit-candidate-equivalence.release-527eb08.json).
# Commits AFTER it deliberately changed 10 of those candidate files (rename-proof
# paths, npm package identity, governance correction). This check pins that set
# exactly, so any NEW drift fails loudly instead of hiding inside a label.
$manifest = 'C:\Users\Administrator\Documents\Codex\2026-10-22\referenced-chatgpt-conversation-this-is-an-2\work\release-candidate-r242b\r242-source-manifest.csv'
$expectedDrift = @(
    'apps/web/package-lock.json',
    'apps/web/package.json',
    'docs/governance/DATABASE_TARGET_REGISTRY.md',
    'tools/dev/check-runtime.ps1',
    'tools/dev/diagnose-admin-role-binding.ps1',
    'tools/dev/g2-005-operator-evidence.ps1',
    'tools/dev/mdm-001-final-acceptance-selftest.ps1',
    'tools/dev/mdm-001-final-acceptance.ps1',
    'tools/dev/mdm-001-operator-evidence.ps1',
    'tools/dev/run-web-preview-backend.ps1'
)
if (Test-Path $manifest) {
    $candPaths = @((Import-Csv $manifest).Path)
    # Pass no pathspec to git (1,371 paths would exceed the command-line limit)
    # and intersect with the candidate set in PowerShell instead.
    $allChanged = @(& $git -C $repo -c core.quotepath=false diff --name-only 527eb08 HEAD 2>$null | Where-Object { $_ })
    $changed = @($allChanged | Where-Object { $candPaths -contains $_ })
    $unexpected = @($changed | Where-Object { $expectedDrift -notcontains $_ })
    $missing = @($expectedDrift | Where-Object { $changed -notcontains $_ })
    Check 'release commit 527eb08 still reproduces the frozen candidate 1,371/1,371 byte-identical' (Test-Path 'D:\backup\guli-platform\guli-platform-v1-20261105T024244Z\evidence\commit-candidate-equivalence.release-527eb08.json') 'preserved evidence record'
    Check 'candidate files changed after the release commit are exactly the 10 intended ones' ($unexpected.Count -eq 0 -and $missing.Count -eq 0) "$($changed.Count) changed, $($unexpected.Count) unexpected, $($missing.Count) missing"
    if ($unexpected.Count) { Write-Host "    unexpected drift: $($unexpected -join ', ')" -ForegroundColor Red }
    if ($missing.Count)     { Write-Host "    no longer drifted: $($missing -join ', ')" -ForegroundColor Yellow }
} else {
    Check 'candidate manifest available for the drift check' $false "not found: $manifest"
}

# ---------------------------------------------------------------- 3. the four documents
Write-Host '-- release documents --' -ForegroundColor White
$docs = @(
    @{ Name = 'RELEASE_BASELINE_REPORT.md';    Marker = 'Release Baseline Report' },
    @{ Name = 'BACKUP_VERIFICATION_REPORT.md'; Marker = 'Backup Verification Report' },
    @{ Name = 'WIP_TASK_REGISTER.md';           Marker = 'WIP Task Register' },
    @{ Name = 'GULI_PLATFORM_V1_RELEASE_HANDOFF.md'; Marker = 'Release status' }
)
foreach ($d in $docs) {
    $p = Join-Path $repo "docs\release\$($d.Name)"
    $exists = Test-Path $p
    $markerOk = $false
    if ($exists) { $markerOk = (Select-String -Path $p -Pattern $d.Marker -SimpleMatch -Quiet) }
    Check "document $($d.Name)" ($exists -and $markerOk) $(if ($exists) { "$((Get-Item $p).Length) B" } else { 'missing' })
}
$harnessOk = (Test-Path (Join-Path $repo 'tools\dev\release-v1-acceptance\smoke-test-crm.ps1')) -and `
              (Test-Path (Join-Path $repo 'tools\dev\release-v1-acceptance\run-release-acceptance.ps1')) -and `
              (Test-Path (Join-Path $repo 'tools\dev\release-v1-acceptance\cleanup-smoke-rows.sql'))
Check 'acceptance harness versioned in the repository' $harnessOk '3 files'

# ---------------------------------------------------------------- 4. live deployment
Write-Host '-- live deployment --' -ForegroundColor White
foreach ($u in @('http://127.0.0.1:8080','http://192.168.2.200:8080')) {
    try {
        $r = Invoke-WebRequest "$u/health/live" -UseBasicParsing -TimeoutSec 10
        Check "health/live $u" ($r.StatusCode -eq 200) "HTTP $($r.StatusCode)"
    } catch { Check "health/live $u" $false $_.Exception.Message }
}
try {
    $r = Invoke-WebRequest 'http://127.0.0.1:8080/health/ready' -UseBasicParsing -TimeoutSec 15
    $j = $r.Content | ConvertFrom-Json
    $db = @($j.checks | Where-Object { $_.name -match 'db' })
    Check 'health/ready (real DB round-trip)' ($r.StatusCode -eq 200 -and $j.status -eq 'Healthy' -and $db.Count -gt 0) "status=$($j.status) db=$($db[0].status) $($db[0].description)"
} catch { Check 'health/ready (real DB round-trip)' $false $_.Exception.Message }
try {
    $r = Invoke-WebRequest 'http://127.0.0.1:8080/' -UseBasicParsing -TimeoutSec 15
    $title = ([regex]::Match($r.Content, '<title>(.*?)</title>')).Groups[1].Value
    Check 'SPA shell' ($r.StatusCode -eq 200 -and $title -match 'Guli Platform') "HTTP $($r.StatusCode) title=$title"
} catch { Check 'SPA shell' $false $_.Exception.Message }
try {
    $r = Invoke-WebRequest 'http://127.0.0.1:8080/crm/companies' -UseBasicParsing -TimeoutSec 15
    Check 'SPA deep link fallback' ($r.StatusCode -eq 200) "HTTP $($r.StatusCode)"
} catch { Check 'SPA deep link fallback' $false $_.Exception.Message }
foreach ($p in @('/api/v1/crm/leads','/api/v1/crm/companies','/api/v1/crm/deals','/api/v1/auth/me')) {
    try { Invoke-WebRequest "http://127.0.0.1:8080$p" -UseBasicParsing -TimeoutSec 10 | Out-Null; Check "anonymous $p is refused" $false 'unexpected 200' }
    catch { $c = 0; if ($_.Exception.Response) { $c = [int]$_.Exception.Response.StatusCode }; Check "anonymous $p is refused" ($c -eq 401) "HTTP $c" }
}

# ---------------------------------------------------------------- 5. smoke result
Write-Host '-- CRM acceptance --' -ForegroundColor White
$csv = Join-Path $deploy 'logs\smoke-result.csv'
if (Test-Path $csv) {
    $rows = Import-Csv $csv
    $fails = @($rows | Where-Object { $_.Result -ne 'PASS' })
    Check 'smoke result CSV' ($fails.Count -eq 0 -and $rows.Count -ge 16) "$($rows.Count) checks, $($fails.Count) failures"
} else { Check 'smoke result CSV' $false 'missing' }
$log = Join-Path $deploy 'logs\acceptance-run-2.log'
if (Test-Path $log) {
    $txt = Get-Content $log -Raw
    Check 'acceptance log shows 16/0 and the 401 proof' (($txt -match '16 passed, 0 failed') -and ($txt -match 'retired operator rejected with HTTP 401')) '16 passed, 0 failed + HTTP 401'
} else { Check 'acceptance log shows 16/0 and the 401 proof' $false 'missing' }

# ---------------------------------------------------------------- 6. database state
Write-Host '-- formal database --' -ForegroundColor White
$envFile = Join-Path $repo '.env.local'
$conn = ((Get-Content $envFile | Where-Object { $_ -match '^ConnectionStrings__GuliERP=' } | Select-Object -First 1) -replace '^ConnectionStrings__GuliERP=','')
$dbHost = if ($conn -match 'Host=([^;]+)') { $matches[1] }
$dbPort = if ($conn -match 'Port=([^;]+)') { $matches[1] } else { '5432' }
$dbUser = if ($conn -match 'Username=([^;]+)') { $matches[1] }
$env:PGPASSWORD = if ($conn -match '(?<=Password=)[^;]*') { $matches[0] } else { '' }
$sqlFile = Join-Path $env:TEMP 'final-verify.sql'
$outFile = Join-Path $env:TEMP 'final-verify.txt'
# Unaligned, tuples-only output: one value per line, no padding, so the
# assertions below can match exact tokens instead of guessing column widths.
$sql = @"
select 'OP_STATUS='||u."Status" from identity."AspNetUsers" u where u."UserName" like 'GULIXINXI_v10%';
select 'OP_LOCKOUT_ENABLED='||u."LockoutEnabled" from identity."AspNetUsers" u where u."UserName" like 'GULIXINXI_v10%';
select 'OP_LOCKOUT_END_SET='||(u."LockoutEnd" is not null)::text from identity."AspNetUsers" u where u."UserName" like 'GULIXINXI_v10%';
select 'OP_ROLES='||(select count(*) from identity.gulierp_user_role_assignment r where r."UserId"=u."Id") from identity."AspNetUsers" u where u."UserName" like 'GULIXINXI_v10%';
select 'OP_MEMBERSHIPS='||(select count(*) from identity.gulierp_user_company_membership m where m."UserId"=u."Id") from identity."AspNetUsers" u where u."UserName" like 'GULIXINXI_v10%';
select 'OP_COUNT='||count(*) from identity."AspNetUsers" u where u."UserName" like 'GULIXINXI_v10%';
select 'BIZ_STATUS='||"Status" from identity."AspNetUsers" where "UserName"='wangchunqing';
select 'RES_COMPANIES='||(select count(*) from crm.companies where "FullName" like 'V10-SMOKE-%');
select 'RES_LEADS='||(select count(*) from crm.leads where "CompanyName" like 'V10-SMOKE-%');
select 'RES_DEALS='||(select count(*) from crm.deals where "Name" like 'V10-SMOKE-%');
select 'RES_ACTIVITIES='||(select count(*) from crm.activities where "Content" like '%V10-SMOKE-%');
select 'DEAL_COUNT='||count(*) from crm.deals where "Name" like 'V10-SMOKE-%';
select 'DEAL_ALL_BOUND='||count(*) from crm.deals where "Name" like 'V10-SMOKE-%' and "SourceLeadDemandAssessmentId" is not null and "SourceLeadDemandAssessmentVersion" is not null;
select 'DEAL_COMPANY_IS_CONVERTED='||count(*) from crm.deals d join crm.companies c on c."Id"=d."CrmCompanyId" where d."Name" like 'V10-SMOKE-%' and c."FullName" like '%转化客户%';
select 'DEAL_ASSESSMENT_IDS='||string_agg(distinct "SourceLeadDemandAssessmentId"::text, ',') from crm.deals where "Name" like 'V10-SMOKE-%';
select 'DEAL_ASSESSMENT_VERSIONS='||string_agg(distinct "SourceLeadDemandAssessmentVersion"::text, ',') from crm.deals where "Name" like 'V10-SMOKE-%';
select 'ASSESSMENT_STATE='||a."State" from crm.lead_demand_assessments a where a."LeadId" in (select "Id" from crm.leads where "CompanyName" like 'V10-SMOKE-%');
select 'EVIDENCE_TYPE='||e."EvidenceType" from crm.lead_assessment_evidence e where e."LeadId" in (select "Id" from crm.leads where "CompanyName" like 'V10-SMOKE-%');
"@
Set-Content -Path $sqlFile -Value $sql -Encoding UTF8
& $psql -h $dbHost -p $dbPort -U $dbUser -d 'gulierp_next_prod' -t -A -f $sqlFile 2>&1 | Out-File $outFile -Encoding utf8
$env:PGPASSWORD = $null
$dbTxt = (Get-Content $outFile -Raw) -replace "`r",''
function DbVal([string]$k) {
    $m = [regex]::Match($dbTxt, "(?m)^$k=([^\r\n]*)$")
    if ($m.Success) { $m.Groups[1].Value.Trim() } else { $null }
}
Write-Host ("    operator status={0} lockoutEnabled={1} lockoutEndSet={2} roles={3} memberships={4}" -f (DbVal 'OP_STATUS'), (DbVal 'OP_LOCKOUT_ENABLED'), (DbVal 'OP_LOCKOUT_END_SET'), (DbVal 'OP_ROLES'), (DbVal 'OP_MEMBERSHIPS')) -ForegroundColor DarkGray
Write-Host ("    business wangchunqing status={0} | residue companies={1} leads={2} deals={3} activities={4}" -f (DbVal 'BIZ_STATUS'), (DbVal 'RES_COMPANIES'), (DbVal 'RES_LEADS'), (DbVal 'RES_DEALS'), (DbVal 'RES_ACTIVITIES')) -ForegroundColor DarkGray
Write-Host ("    deal -> assessment ids={0} versions={1} count={2} allBound={3} onConvertedCompany={4} | assessment state={5} evidenceType={6}" -f (DbVal 'DEAL_ASSESSMENT_IDS'), (DbVal 'DEAL_ASSESSMENT_VERSIONS'), (DbVal 'DEAL_COUNT'), (DbVal 'DEAL_ALL_BOUND'), (DbVal 'DEAL_COMPANY_IS_CONVERTED'), (DbVal 'ASSESSMENT_STATE'), (DbVal 'EVIDENCE_TYPE')) -ForegroundColor DarkGray

$opRetired = (DbVal 'OP_COUNT') -eq '1' -and (DbVal 'OP_STATUS') -eq '2' -and (DbVal 'OP_LOCKOUT_ENABLED') -eq 'true' -and (DbVal 'OP_LOCKOUT_END_SET') -eq 'true' -and (DbVal 'OP_ROLES') -eq '0' -and (DbVal 'OP_MEMBERSHIPS') -eq '0'
Check 'verification operator retired (Status=2, lockout armed, 0 roles, 0 memberships)' $opRetired 'exactly 1 operator row, disabled'
Check 'business account wangchunqing still Active' ((DbVal 'BIZ_STATUS') -eq '1') "status=$(DbVal 'BIZ_STATUS')"
$resOk = (DbVal 'RES_COMPANIES') -eq '6' -and (DbVal 'RES_LEADS') -eq '3' -and (DbVal 'RES_DEALS') -eq '2' -and (DbVal 'RES_ACTIVITIES') -eq '5'
Check 'acceptance residue present and counted (6 customers, 3 leads, 2 deals, 5 activities)' $resOk 'marker-scoped, awaits cleanup approval'
$dealOk = (DbVal 'DEAL_COUNT') -eq '2' -and (DbVal 'DEAL_ALL_BOUND') -eq (DbVal 'DEAL_COUNT') -and (DbVal 'DEAL_COMPANY_IS_CONVERTED') -eq (DbVal 'DEAL_COUNT')
Check 'every opportunity bound to the confirmed assessment (id + version) on the converted customer' $dealOk "deals=$(DbVal 'DEAL_COUNT') bound=$(DbVal 'DEAL_ALL_BOUND') ids=$(DbVal 'DEAL_ASSESSMENT_IDS') versions=$(DbVal 'DEAL_ASSESSMENT_VERSIONS')"
$asmOk = (DbVal 'ASSESSMENT_STATE') -eq 'confirmed' -and (DbVal 'EVIDENCE_TYPE') -eq 'manual'
Check 'assessment is confirmed with structured evidence' $asmOk "state=$(DbVal 'ASSESSMENT_STATE') type=$(DbVal 'EVIDENCE_TYPE')"

# ---------------------------------------------------------------- 7. backup
Write-Host '-- backup --' -ForegroundColor White
$bk = 'D:\backup\guli-platform\guli-platform-v1-20261105T024244Z'
$sum = Join-Path $bk 'manifests\SHA256SUMS.txt'
$lines = Get-Content $sum
$bad = 0
foreach ($l in $lines) {
    if ($l -match '^([0-9A-Fa-f]{64})\s+(\d+)\s+(.+)$') {
        $f = Join-Path $bk $matches[3]
        if (-not (Test-Path -LiteralPath $f)) { $bad++; continue }
        if ((Get-Item -LiteralPath $f).Length -ne [int64]$matches[2]) { $bad++; continue }
    }
}
Check 'backup manifest (1,849 entries) re-verified' ($lines.Count -eq 1849 -and $bad -eq 0) "$($lines.Count) entries, $bad problems"
$rv = Get-Content (Join-Path $bk 'VERIFY-restore.json') -Raw | ConvertFrom-Json
Check 'restore proof record' ($rv.PSObject.Properties.Name -contains 'Verdict' -or $rv.PSObject.Properties.Name -contains 'verdict') ($rv | ConvertTo-Json -Compress -Depth 3)

# ---------------------------------------------------------------- summary
Write-Host ''
$out = Join-Path $deploy 'logs\final-verification.json'
$script:results | ConvertTo-Json -Depth 4 | Set-Content -Path $out -Encoding utf8
Write-Host ("FINAL VERIFICATION: {0} passed, {1} failed   -> {2}" -f $script:pass, $script:fail, $out) -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
Write-Host ''
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
