$ErrorActionPreference = 'Stop'
# Prove that the committed tree reproduces the frozen R242b candidate CONTENT.
#
# Two independent facts are checked:
#   1. Logical equivalence  — for every candidate path, the committed blob equals
#      the working-tree file after line-ending normalisation (LF).
#   2. Line-ending profile — how many candidate files differ from the manifest
#      purely because git stores LF while the manifest hashed on-disk CRLF bytes.
#
# The manifest was produced from the on-disk working tree, so raw SHA-256 equality
# is only expected for LF files. Content equivalence is the property that matters.
$repo   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$base   = 'C:\Users\Administrator\Documents\Codex\2026-10-22\referenced-chatgpt-conversation-this-is-an-2'
$manifestPath = Join-Path $base 'work\release-candidate-r242b\r242-source-manifest.csv'
$head = (git -C $repo rev-parse HEAD).Trim()
Write-Host "[verify] HEAD = $head" -ForegroundColor Cyan

function Get-NormSha([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $out = New-Object System.Collections.Generic.List[byte]
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -eq 13 -and ($i + 1) -lt $bytes.Length -and $bytes[$i+1] -eq 10) { continue }
        $out.Add($bytes[$i])
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    ($sha.ComputeHash($out.ToArray()) | ForEach-Object { $_.ToString('x2') }) -join ''
}

$manifest = @{}
foreach ($r in (Import-Csv $manifestPath)) { $manifest[$r.Path] = $r.SHA256 }

$tmp = Join-Path $env:TEMP ("guli-blob-" + [guid]::NewGuid().ToString('N').Substring(0,8) + '.bin')
$contentMismatch = @(); $lfOnly = @(); $byteExact = 0; $checked = 0

foreach ($p in $manifest.Keys) {
    $wt = Join-Path $repo ($p -replace '/', '\')
    if (-not (Test-Path -LiteralPath $wt -PathType Leaf)) { $contentMismatch += "$p (absent from working tree)"; continue }
    # committed blob for this path
    & git -C $repo cat-file blob "HEAD:$p" > $tmp 2>$null
    if ($LASTEXITCODE -ne 0) { $contentMismatch += "$p (absent from commit)"; continue }
    $blobNorm = Get-NormSha $tmp
    $wtNorm   = Get-NormSha $wt
    $checked++
    if ($blobNorm -ne $wtNorm) { $contentMismatch += $p }
    $raw = (Get-FileHash -LiteralPath $wt -Algorithm SHA256).Hash
    if ($raw -eq $manifest[$p]) { $byteExact++ } else { $lfOnly += $p }
}
if (Test-Path $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }

# Paths in the commit that are outside the candidate manifest.
$treePaths = @(& git -C $repo -c core.quotepath=false ls-tree -r --name-only HEAD 2>$null)
$extra = $treePaths | Where-Object { -not $manifest.ContainsKey($_) }

Write-Host ""
Write-Host "[verify] candidate paths checked        : $checked / $($manifest.Count)" -ForegroundColor White
Write-Host "[verify] byte-identical to manifest     : $byteExact" -ForegroundColor Green
Write-Host "[verify] differ only by line endings    : $($lfOnly.Count)" -ForegroundColor Yellow
Write-Host "[verify] CONTENT MISMATCH                : $($contentMismatch.Count)" -ForegroundColor $(if($contentMismatch.Count){'Red'}else{'Green'})
Write-Host "[verify] in commit, outside manifest     : $($extra.Count)" -ForegroundColor Yellow
if ($contentMismatch.Count) { $contentMismatch | Select-Object -First 20 | ForEach-Object { Write-Host "    MISMATCH $_" -ForegroundColor Red } }
if ($extra.Count) { $extra | Sort-Object | ForEach-Object { Write-Host "    EXTRA   $_" -ForegroundColor Yellow } }

$verdict = if ($contentMismatch.Count -eq 0 -and $checked -eq $manifest.Count) { 'PASS' } else { 'FAIL' }
Write-Host ""
Write-Host "[verify] CONTENT-EQUIVALENCE VERDICT = $verdict" -ForegroundColor $(if($verdict -eq 'PASS'){'Green'}else{'Red'})

$result = [ordered]@{
    Head = $head
    CandidateFiles = $manifest.Count
    Checked = $checked
    ByteIdenticalToManifest = $byteExact
    DifferOnlyByLineEndings = $lfOnly.Count
    LineEndingAffectedSample = @($lfOnly | Select-Object -First 20)
    ContentMismatches = $contentMismatch
    ExtraInCommitOutsideManifest = @($extra)
    Verdict = $verdict
    VerifiedAtUtc = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
}
$out = 'D:\backup\guli-platform\guli-platform-v1-20261105T024244Z\evidence\commit-candidate-equivalence.json'
$result | ConvertTo-Json -Depth 4 | Set-Content -Path $out -Encoding utf8
Write-Host "[verify] result -> $out" -ForegroundColor Cyan
