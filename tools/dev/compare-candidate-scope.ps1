$ErrorActionPreference = 'Stop'
$repo = 'D:\guli\projects\gulierp-next'
$base = 'C:\Users\Administrator\Documents\Codex\2026-10-22\referenced-chatgpt-conversation-this-is-an-2'
$manifest = Join-Path $base 'work\release-candidate-r242b\r242-source-manifest.csv'

$rows = Import-Csv -Path $manifest
Write-Output "Candidate manifest rows: $($rows.Count)"
$pathCol = ($rows[0].PSObject.Properties.Name | Where-Object { $_ -match 'Path' } | Select-Object -First 1)
$hashCol = ($rows[0].PSObject.Properties.Name | Where-Object { $_ -match 'SHA' } | Select-Object -First 1)
Write-Output "Manifest columns: path='$pathCol' hash='$hashCol'"

$cand = @{}
foreach ($r in $rows) { $cand[$r.$pathCol] = $r.$hashCol }

Push-Location $repo
try { $current = & git -c core.quotepath=false ls-files -c -o --exclude-standard } finally { Pop-Location }
$cur = @{}; foreach ($p in $current) { $cur[$p] = $true }

$inCandNotCur = $cand.Keys | Where-Object { -not $cur.ContainsKey($_) }
$inCurNotCand = $cur.Keys | Where-Object { -not $cand.ContainsKey($_) }

Write-Output ""
Write-Output "== candidate files NOT present in current worktree: $($inCandNotCur.Count) =="
$inCandNotCur | Select-Object -First 20 | ForEach-Object { "   $_" }
Write-Output ""
Write-Output "== current files NOT in candidate manifest: $($inCurNotCand.Count) =="
$inCurNotCand | ForEach-Object { "   $_" }

# Hash drift check for the overlap
$drift = @()
$checked = 0
foreach ($p in $cand.Keys) {
    if (-not $cur.ContainsKey($p)) { continue }
    $f = Join-Path $repo $p
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { continue }
    $h = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash
    $checked++
    if ($h -ne $cand[$p]) { $drift += $p }
}
Write-Output ""
Write-Output "== hash-checked overlap: $checked files; drift: $($drift.Count) =="
$drift | Select-Object -First 20 | ForEach-Object { "   DRIFT $_" }
