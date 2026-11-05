$ErrorActionPreference = 'Stop'
$repo = 'D:\guli\projects\gulierp-next'
$out  = 'D:\backup\guli-platform\guli-platform-v1-20261105T024244Z\evidence\sensitive-scan-precommit.json'
$j = Get-Content $out -Raw | ConvertFrom-Json

# Mask anything that looks like an assigned literal so no value is ever printed.
function Mask([string]$line) {
    $s = $line
    $s = $s -replace '(?i)(password|pwd|secret|token|api[_-]?key)(\s*[:=]\s*)("[^"]*"|''[^'']*''|[^;,)\s]+)', '$1$2<masked>'
    $s = $s -replace '(?i)(Authorization\s*:\s*Bearer\s+)(\S+)', '$1<masked>'
    $s = $s -replace '(?i)((?:_PASS|_PASSWORD|_SECRET|_TOKEN|_API_KEY)\s*=\s*)(""[^""]*""|''[^'']*'')', '$1<masked>'
    return $s
}

foreach ($c in $j.candidates) {
    $f = Join-Path $repo $c.path
    Write-Output ("--- {0}:{1} ---" -f $c.path, $c.line)
    if (-not (Test-Path -LiteralPath $f)) { Write-Output "    <file missing>"; continue }
    $lines = Get-Content -LiteralPath $f
    $n = [int]$c.line
    for ($i = [Math]::Max(1, $n - 2); $i -le [Math]::Min($lines.Count, $n + 2); $i++) {
        $mark = if ($i -eq $n) { '>>' } else { '  ' }
        Write-Output ("    {0} {1,5}: {2}" -f $mark, $i, (Mask $lines[$i-1]))
    }
    Write-Output ""
}
