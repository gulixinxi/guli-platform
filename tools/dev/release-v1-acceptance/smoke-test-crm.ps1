#requires -Version 5.1
<#
.SYNOPSIS
    Guli Platform V1.0 — authenticated CRM business smoke test.

.DESCRIPTION
    Exercises the release-critical business path against the deployed instance:
        login -> customer (CRM company) -> follow-up (activity) -> lead ->
        confirmed demand assessment -> lead conversion -> opportunity (deal)
        -> persistence read-back.

    The password is read from Windows Credential Manager and is never printed,
    never passed on the command line and never written to any log or report.

    Every record it creates carries the marker in $Marker so the rows are
    identifiable and can be reviewed or removed by the operator.

.EXAMPLE
    pwsh smoke-test-crm.ps1 -CredentialTarget 'GuliERP-...'
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:8080',
    [string]$UserName,
    [string]$CredentialTarget,
    [string]$TenantCode = 'GULIXINXI',
    [string]$Marker = 'V10-SMOKE',
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$script:pass = 0; $script:fail = 0
$script:results = @()

function Say([string]$m, [string]$c = 'Cyan') { Write-Host $m -ForegroundColor $c }
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { $script:pass++ } else { $script:fail++ }
    $script:results += [pscustomobject]@{ Step = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Detail = $detail }
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    $col = if ($ok) { 'Green' } else { 'Red' }
    Say ("  [{0}] {1}  {2}" -f $tag, $name, $detail) $col
}

# ---------------------------------------------------------------- password source
Add-Type @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class WinCred {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NativeCredential {
        public UInt32 Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public UInt32 CredentialBlobSize;
        public IntPtr CredentialBlob;
        public UInt32 Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint="CredReadW", SetLastError=true, CharSet=CharSet.Unicode)]
    static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", SetLastError=true)]
    static extern void CredFree(IntPtr handle);
    public static string Read(string target) {
        IntPtr h;
        if (!CredRead(target, 1, 0, out h)) throw new Win32Exception(Marshal.GetLastWin32Error(), "credential not found");
        try {
            var c = Marshal.PtrToStructure<NativeCredential>(h);
            var bytes = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        } finally { CredFree(h); }
    }
}
"@

# The password comes from Windows Credential Manager, or — for an in-process run
# that just provisioned the operator — from the GULI_SMOKE_PASSWORD environment
# variable. It is never taken from the command line and never printed.
$envPassword = $env:GULI_SMOKE_PASSWORD
if ($envPassword) {
    $password = $envPassword
    Say 'Password supplied in-process via GULI_SMOKE_PASSWORD (not printed).' 'DarkGray'
} else {
    if (-not $CredentialTarget) { throw 'Provide -CredentialTarget (Windows Credential Manager target) or set GULI_SMOKE_PASSWORD.' }
    $password = [WinCred]::Read($CredentialTarget)
    if ([string]::IsNullOrWhiteSpace($password)) { throw 'Credential target returned an empty password.' }
    Say ("Credential resolved from Windows Credential Manager (not printed). User: $UserName") 'DarkGray'
}

$stamp = Get-Date -Format 'yyyyMMddHHmmss'
$marker = "$Marker-$stamp"

# ---------------------------------------------------------------- http helpers
$session = $null
$csrf = $null
function Get-Csrf {
    $r = Invoke-WebRequest "$BaseUrl/api/v1/auth/csrf" -UseBasicParsing -TimeoutSec 15 -SessionVariable s -ErrorAction Stop
    $script:session = $s
    $script:csrf = ($r.Content | ConvertFrom-Json).requestToken
}
function Api([string]$method, [string]$path, $body) {
    $headers = @{}
    if ($script:csrf) { $headers['X-CSRF-TOKEN'] = $script:csrf }
    $params = @{ Uri = "$BaseUrl$path"; Method = $method; Headers = $headers; WebSession = $script:session; TimeoutSec = 30; UseBasicParsing = $true }
    if ($null -ne $body) { $params['ContentType'] = 'application/json'; $params['Body'] = ($body | ConvertTo-Json -Depth 8 -Compress) }
    try {
        $r = Invoke-WebRequest @params
        $parsed = $null
        if ($r.Content) { try { $parsed = $r.Content | ConvertFrom-Json } catch { $parsed = $r.Content } }
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $parsed; Raw = $r.Content }
    } catch {
        $code = 0; $payload = $null; $raw = ''
        $err = $_
        if ($err.Exception.Response) { $code = [int]$err.Exception.Response.StatusCode }
        # PowerShell 7 carries the response body in ErrorDetails.Message; the
        # response stream is already drained and yields an empty string.
        # Reading only the stream is what made every 400 look bodyless.
        if ($err.ErrorDetails -and $err.ErrorDetails.Message) { $raw = $err.ErrorDetails.Message }
        if (-not $raw -and $err.Exception.Response) {
            try { $sr = New-Object IO.StreamReader($err.Exception.Response.GetResponseStream()); $raw = $sr.ReadToEnd() } catch { }
        }
        if ($raw) { try { $payload = $raw | ConvertFrom-Json } catch { } }
        return [pscustomobject]@{ Status = $code; Body = $payload; Raw = $raw }
    }
}

Say ''
Say '=== Guli Platform V1.0 — CRM business smoke test ===' 'Cyan'
Say ("  target   : $BaseUrl")
Say ("  marker   : $marker")

# 1. login
Get-Csrf
$login = Api 'POST' '/api/v1/auth/login' @{ userName = $UserName; password = $password; tenantCode = $TenantCode }
Check 'login' ($login.Status -eq 200) ("HTTP {0}" -f $login.Status)
if ($login.Status -ne 200) {
    Say ("  body: " + ($login.Raw | Out-String).Trim()) 'Red'
    $script:results | ConvertTo-Json -Depth 3 | Write-Output
    exit 2
}

# 2. identity
$me = Api 'GET' '/api/v1/auth/me' $null
$codes = @()
if ($me.Body) { $codes = @($me.Body.permissionCodes) }
Check 'auth/me' ($me.Status -eq 200) ("HTTP {0} user={1} tenant={2} company={3}" -f $me.Status, $me.Body.userName, $me.Body.tenantCode, $me.Body.companyCode)
Check 'CRM permissions present' (($codes -contains 'crm.company.manage') -and ($codes -contains 'crm.activity.manage') -and ($codes -contains 'crm.deal.manage')) ("codes={0}" -f ($codes -join ','))
if ($me.Body.mustChangePassword -eq $true) {
    Say '  account requires a password change — performing it and re-authenticating' 'Yellow'
    $newPassword = 'V10-Smoke-' + ([guid]::NewGuid().ToString('N').Substring(0,16)) + '!'
    $chg = Api 'POST' '/api/v1/auth/password/change' @{ currentPassword = $password; newPassword = $newPassword }
    Check 'password change' ($chg.Status -eq 204 -or $chg.Status -eq 200) ("HTTP {0}" -f $chg.Status)
    Get-Csrf
    $login2 = Api 'POST' '/api/v1/auth/login' @{ userName = $UserName; password = $newPassword; tenantCode = $TenantCode }
    Check 're-login after password change' ($login2.Status -eq 200) ("HTTP {0}" -f $login2.Status)
}

# 3. customer
$companyName = "$marker 验收客户"
$company = Api 'POST' '/api/v1/crm/companies' @{
    fullName = $companyName; city = 1; industry = 6; source = 99; hasWebsite = $false
    websiteStatus = $null; scale = $null; revenue = $null; sourceUrl = $null; shortName = $null
}
$companyId = $null
if ($company.Status -ge 200 -and $company.Status -lt 300) { $companyId = $company.Body.id }
Check 'create customer (crm/companies)' ([bool]$companyId) ("HTTP {0} id={1} name={2}" -f $company.Status, $companyId, $companyName)
if (-not $companyId) { Say ("  body: " + ($company.Raw | Out-String).Trim()) 'Red' }

# 4. follow-up
$activityId = $null
if ($companyId) {
    $act = Api 'POST' '/api/v1/crm/activities' @{
        crmCompanyId = $companyId; followType = 0; content = "$marker 首次电话跟进：确认建站需求"
        nextAction = "$marker 安排上门拜访"; nextActionDate = (Get-Date).AddDays(3).ToString('yyyy-MM-dd')
    }
    if ($act.Status -ge 200 -and $act.Status -lt 300) { $activityId = $act.Body.id }
    Check 'create follow-up (crm/activities)' ([bool]$activityId) ("HTTP {0} id={1}" -f $act.Status, $activityId)
    if (-not $activityId) { Say ("  body: " + ($act.Raw | Out-String).Trim()) 'Red' }
}

# 5. lead
$leadId = $null
$lead = Api 'POST' '/api/v1/crm/leads' @{ companyName = "$marker 线索企业"; source = 99; city = 1; industry = 6 }
if ($lead.Status -ge 200 -and $lead.Status -lt 300) { $leadId = $lead.Body.id }
Check 'create lead (crm/leads)' ([bool]$leadId) ("HTTP {0} id={1}" -f $lead.Status, $leadId)
if (-not $leadId) { Say ("  body: " + ($lead.Raw | Out-String).Trim()) 'Red' }

# 6. confirmed demand assessment
# The service answers with the full four-direction projection
# ({ leadId, items: [{ id, category, state, version, ... }] }), so the
# assessment id/version live under items[], not at the top level.
# Version 0 is legitimate for a freshly created assessment, so it must not
# be conflated with "version missing".
$assessmentId = $null; $assessmentVersion = $null; $assessmentVersionFound = $false
if ($leadId) {
    $asm = Api 'PUT' "/api/v1/crm/leads/$leadId/demand-assessments/website" @{
        state = 'confirmed'; confidence = 'high'
        evidenceItems = @(@{ evidenceType = 'manual'; url = 'https://example.invalid/release-smoke'; collectedAt = (Get-Date).ToUniversalTime().ToString('o'); verificationStatus = 'verified'; note = "$marker release verification evidence" })
        reason = "$marker 发布验收：已确认建站需求"
        nextAction = "$marker 进入商机评估"
    }
    if ($asm.Status -ge 200 -and $asm.Status -lt 300) {
        $entry = @($asm.Body.items) | Where-Object { $_.category -eq 'website' } | Select-Object -First 1
        if ($entry -and $entry.id) {
            $assessmentId = $entry.id
            if ($null -ne $entry.version) { $assessmentVersion = [int]$entry.version; $assessmentVersionFound = $true }
        }
    }
    Check 'confirm lead demand assessment' ([bool]($assessmentId -and $assessmentVersionFound)) ("HTTP {0} id={1} version={2}" -f $asm.Status, $assessmentId, $assessmentVersion)
    if (-not $assessmentId -or -not $assessmentVersionFound) { Say ("  body: " + ($asm.Raw | Out-String).Trim()) 'Red' }
}

# 6b. read the assessment back — state must be 'confirmed' with evidence attached
if ($leadId -and $assessmentId) {
    $readBack = Api 'GET' "/api/v1/crm/leads/$leadId/demand-assessments" $null
    $confirmed = $false; $evidenceCount = 0; $state = '-'
    if ($readBack.Status -eq 200) {
        $entry = @($readBack.Body.items) | Where-Object { $_.category -eq 'website' } | Select-Object -First 1
        if ($entry) {
            $state = $entry.state
            $confirmed = ($state -eq 'confirmed')
            $evidenceCount = @($entry.evidenceItems).Count
        }
    }
    Check 'assessment read-back (state=confirmed)' ($confirmed -and $evidenceCount -gt 0) ("HTTP {0} state={1} evidence={2}" -f $readBack.Status, $state, $evidenceCount)
}

# 7. convert the lead into a customer master record
# A formal opportunity can only be created from a lead that has been
# CONVERTED: the deal gate compares lead.ConvertedToCompanyId against
# deal.CrmCompanyId, and it rejects a company that did not come from the
# source lead.
$convertedCompanyId = $null
if ($leadId -and $assessmentId) {
    $conv = Api 'POST' "/api/v1/crm/leads/$leadId/convert" @{
        leadId = $leadId; fullName = "$marker 转化客户"; city = 1; industry = 6
        hasWebsite = $false; websiteStatus = $null; scale = $null; revenue = $null
        remarks = "$marker 线索转化验收"
    }
    if ($conv.Status -ge 200 -and $conv.Status -lt 300) { $convertedCompanyId = $conv.Body.id }
    Check 'convert lead to customer' ([bool]$convertedCompanyId) ("HTTP {0} id={1}" -f $conv.Status, $convertedCompanyId)
    if (-not $convertedCompanyId) { Say ("  body: " + ($conv.Raw | Out-String).Trim()) 'Red' }
}

# 8. opportunity (商机) — must carry the confirmed assessment id AND its version
$dealId = $null
if ($convertedCompanyId -and $assessmentId -and $assessmentVersionFound) {
    $dealBody = @{
        name = "$marker 建站商机"; crmCompanyId = $convertedCompanyId; category = 0; expectedAmount = 58000
        stage = 1; expectedCloseMonth = (Get-Date).AddMonths(2).ToString('yyyy-MM'); winRate = 30
        sourceLeadDemandAssessmentId = [long]$assessmentId
        sourceLeadDemandAssessmentVersion = [int]$assessmentVersion
    }
    $deal = Api 'POST' '/api/v1/crm/deals' $dealBody
    if ($deal.Status -ge 200 -and $deal.Status -lt 300) { $dealId = $deal.Body.id }
    Check 'create opportunity (crm/deals)' ([bool]$dealId) ("HTTP {0} id={1}" -f $deal.Status, $dealId)
    if (-not $dealId) { Say ("  body: " + ($deal.Raw | Out-String).Trim()) 'Red' }
}

# 9. persistence read-back
# Every read-back is guarded on a NON-NULL id AND matched against the object's
# own id. Comparing a null target against a list item that carries no id
# would match and report a false PASS.
$company = Api 'GET' "/api/v1/crm/companies/$companyId" $null
$companyFound = ($company.Status -eq 200 -and $companyId -and $null -ne $company.Body.id)
Check 'persistence: customer readable' $companyFound ("HTTP {0} id={1}" -f $company.Status, $companyId)

$activity = Api 'GET' "/api/v1/crm/activities/$activityId" $null
$activityFound = ($activity.Status -eq 200 -and $activityId -and $null -ne $activity.Body.id)
Check 'persistence: follow-up readable' $activityFound ("HTTP {0} id={1}" -f $activity.Status, $activityId)

$converted = Api 'GET' "/api/v1/crm/companies/$convertedCompanyId" $null
$convertedFound = ($converted.Status -eq 200 -and $convertedCompanyId -and $null -ne $converted.Body.id)
Check 'persistence: converted customer readable' $convertedFound ("HTTP {0} id={1}" -f $converted.Status, $convertedCompanyId)

$deal = Api 'GET' "/api/v1/crm/deals/$dealId" $null
$dealFound = ($deal.Status -eq 200 -and $dealId -and $null -ne $deal.Body.id)
Check 'persistence: opportunity readable' $dealFound ("HTTP {0} id={1}" -f $deal.Status, $dealId)

$leadRead = Api 'GET' "/api/v1/crm/leads/$leadId" $null
$leadFound = ($leadRead.Status -eq 200 -and $leadId -and $null -ne $leadRead.Body.id)
Check 'persistence: lead readable' $leadFound ("HTTP {0} id={1}" -f $leadRead.Status, $leadId)

# 9. access boundary re-check (the session must not be able to switch company)
$switch = Api 'POST' '/api/v1/auth/company/switch' @{ targetCompanyId = 999999 }
Check 'cross-company switch rejected' ($switch.Status -eq 403 -or $switch.Status -eq 400) ("HTTP {0}" -f $switch.Status)

Say ''
Say ("SMOKE RESULT: {0} passed, {1} failed   marker={2}" -f $script:pass, $script:fail, $marker) $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })

if ($Json) { $script:results | ConvertTo-Json -Depth 3 | Write-Output }
# The result CSV is written next to this script, so the harness works from the
# deployment root and from the repository copy alike.
$logDir = Join-Path $PSScriptRoot 'logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$script:results | Export-Csv -Path (Join-Path $logDir 'smoke-result.csv') -NoTypeInformation -Encoding utf8
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
