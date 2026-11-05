$ErrorActionPreference = 'Stop'
$repo = 'D:\guli\projects\gulierp-next'
Push-Location $repo
try {
    $mod  = @(& git -c core.quotepath=false diff --name-only 2>$null)
    $untr = @(& git -c core.quotepath=false ls-files -o --exclude-standard 2>$null)
} finally { Pop-Location }

function Get-Area([string]$p) {
    switch -Regex ($p) {
        '^modules/crm/'          { return 'CRM domain + application + infrastructure (incl. migrations)' }
        '^modules/identity/'     { return 'Identity (CRM role packs, org admin, seed)' }
        '^modules/foundation/'   { return 'Foundation (shared error codes)' }
        '^modules/(sales|purchase|mdm|document-kernel)/' { return 'Other ERP modules (sales/purchase/mdm/doc-kernel)' }
        '^apps/api/'             { return 'API host (CRM endpoints, org endpoints, guards, config)' }
        '^apps/web/src/views/crm/'    { return 'Web — CRM pages' }
        '^apps/web/src/views/(system|auth)/' { return 'Web — identity/org + login pages' }
        '^apps/web/src/views/'   { return 'Web — other module pages' }
        '^apps/web/src/(api|types|utils|layout|router|components|design-system)/' { return 'Web — shared api/types/utils/layout/router/components' }
        '^apps/web/(index.html|package.json|package-lock.json)' { return 'Web — app shell + dependency manifest' }
        '^tests/GuliERP\.Crm'    { return 'Tests — CRM' }
        '^tests/'                { return 'Tests — other suites' }
        '^tools/'                { return 'Tools / release ops scripts' }
        '^docs/'                 { return 'Docs & governance' }
        '^outputs/'              { return 'Agent scratch reports (outputs/)' }
        default                  { return 'Other' }
    }
}

function Show-Groups([string]$title, [string[]]$paths, [switch]$Detail) {
    Write-Output ""
    Write-Output "########## $title = $($paths.Count) ##########"
    $map = @{}
    foreach ($p in $paths) {
        $a = Get-Area $p
        if (-not $map.ContainsKey($a)) { $map[$a] = New-Object System.Collections.ArrayList }
        [void]$map[$a].Add($p)
    }
    foreach ($k in ($map.Keys | Sort-Object)) {
        Write-Output ("{0,5}  {1}" -f $map[$k].Count, $k)
    }
    if ($Detail) {
        foreach ($k in ($map.Keys | Sort-Object)) {
            Write-Output ""
            Write-Output "--- $k ($($map[$k].Count)) ---"
            foreach ($p in ($map[$k] | Sort-Object)) { Write-Output "    $p" }
        }
    }
}

Show-Groups 'MODIFIED (tracked)' $mod
Show-Groups 'UNTRACKED (new)' $untr
Show-Groups 'DETAIL — modified, excluding CRM core/pages/tests' $mod -Detail
