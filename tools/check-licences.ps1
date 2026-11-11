<#
.SYNOPSIS
    Fails when a NuGet package OpenBook uses, directly or through another, has a licence the project can't take.

.DESCRIPTION
    OpenBook is AGPL-3.0-or-later, so every dependency must be under a permissive licence compatible with it (MIT,
    BSD, Apache-2.0 and the like; see CLAUDE.md). This reads every package the solution resolves (dotnet list package
    --include-transitive, so restore first) and the licence expression in each package's .nuspec. A package that gives
    its licence only as a file or a link can't be read automatically: it passes only once someone has read the licence
    and listed it in $Reviewed below, with what it is. PDFsharp-MigraDoc must stay at 6.2.4, its last MIT release (later
    ones are AGPL or commercial). Exits 1 when something fails, so the CI workflow stops.

.EXAMPLE
    powershell -File tools/check-licences.ps1
#>
param([string]$Solution = 'OpenBook.slnx')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# SPDX identifiers that may be used: permissive and compatible with the AGPL. Anything else fails until it's discussed.
$Allowed = @(
    'MIT', 'MIT-0', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', '0BSD', 'ISC', 'Zlib', 'PostgreSQL', 'BSL-1.0',
    'Unlicense', 'CC0-1.0', 'OFL-1.1'
)

# Packages whose .nuspec has no licence expression, read by hand: id → what the licence is and when it was checked.
$Reviewed = @{
    'xunit.abstractions' = 'Apache-2.0 (license.txt in the xunit repository; tests only), read 2026-11-11'
}

# Pins that must hold whatever a licence says.
$Pinned = @{
    'PDFsharp-MigraDoc' = '6.2.4'
}

function Test-Expression([string]$expression) {
    # "A OR B" passes when either does, "A AND B" when both do; "X WITH exception" counts as X.
    $text = $expression -replace '[()]', ' '
    foreach ($choice in ($text -split '\s+OR\s+')) {
        $all = $true
        foreach ($part in ($choice -split '\s+AND\s+')) {
            $id = (($part.Trim() -split '\s+WITH\s+')[0]).Trim().TrimEnd('+')
            if ($Allowed -notcontains $id) { $all = $false }
        }
        if ($all) { return $true }
    }
    return $false
}

Push-Location $root
try {
    $json = & dotnet list $Solution package --include-transitive --format json 2>$null
    if ($LASTEXITCODE -ne 0) { throw "dotnet list package failed: restore the solution first (dotnet restore)." }
    $listing = ($json -join "`n") | ConvertFrom-Json

    $cache = $env:NUGET_PACKAGES
    if (-not $cache) {
        $line = (& dotnet nuget locals global-packages --list) -join ''
        $cache = ($line -replace '^\s*global-packages:\s*', '').Trim()
    }

    $packages = @{}
    foreach ($project in $listing.projects) {
        foreach ($framework in @($project.frameworks)) {
            foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                if ($package) { $packages["$($package.id)|$($package.resolvedVersion)"] = $package }
            }
        }
    }

    $failures = @()
    $counts = @{}
    foreach ($key in ($packages.Keys | Sort-Object)) {
        $id, $version = $key -split '\|', 2
        if ($Pinned.ContainsKey($id) -and $version -ne $Pinned[$id]) {
            $failures += "$id $version`: must stay at $($Pinned[$id]) (the last release under a licence OpenBook can use)."
        }

        $nuspec = Join-Path $cache (Join-Path $id.ToLowerInvariant() (Join-Path $version.ToLowerInvariant() "$($id.ToLowerInvariant()).nuspec"))
        if (-not (Test-Path $nuspec)) { $failures += "$id $version`: not in the package cache ($nuspec); restore first."; continue }
        $document = New-Object System.Xml.XmlDocument
        $document.Load($nuspec)
        $license = $document.GetElementsByTagName('license') | Select-Object -First 1
        $expression = if ($license -and $license.GetAttribute('type') -eq 'expression') { $license.InnerText.Trim() } else { $null }

        if ($expression) {
            if (Test-Expression $expression) { $counts[$expression] = 1 + [int]$counts[$expression] }
            else { $failures += "$id $version`: $expression isn't on the list of licences OpenBook can use." }
        }
        elseif ($Reviewed.ContainsKey($id)) { $counts['reviewed by hand'] = 1 + [int]$counts['reviewed by hand'] }
        else {
            $url = ($document.GetElementsByTagName('licenseUrl') | Select-Object -First 1)
            $where = if ($license) { "a file in the package ($($license.InnerText.Trim()))" } elseif ($url) { $url.InnerText.Trim() } else { 'nowhere' }
            $failures += "$id $version`: its licence is given as $where. Read it, and if it's permissive, add the package to `$Reviewed in tools/check-licences.ps1."
        }
    }

    $summary = ($counts.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Value) $($_.Name)" }) -join ', '
    Write-Host "$($packages.Count) packages checked: $summary."
    if ($failures.Count -gt 0) {
        Write-Host ''
        Write-Host "$($failures.Count) package(s) can't be used as they are:"
        $failures | ForEach-Object { Write-Host "  $_" }
        if ($env:GITHUB_STEP_SUMMARY) {
            $lines = @("### Package licences", '') + ($failures | ForEach-Object { "- $_" })
            Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $lines -Encoding utf8
        }
        exit 1
    }
    exit 0
}
finally {
    Pop-Location
}
