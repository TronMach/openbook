<#
.SYNOPSIS
    Works out which test classes cover what changed since -Base, for run-tests.ps1 -Changed.

.DESCRIPTION
    Changed files are those differing from where this branch left -Base (committed, uncommitted and new), and
    artifacts\test-map.json (tools/build-test-map.ps1) says which integration test classes run each one.
    - Text only (docs, Markdown, packaging, most of tools) needs no tests.
    - Migrations, build files (projects, Directory.Build.props, global.json, the solution), tools/pgtest.ps1 and
      anything the rules below can't place: everything.
    - A source file in the map: the classes that ran it. A test file: the test classes it declares (not through the
      map, where xUnit's finding the tests puts such files under every class); a helper file: the classes using it.
    - A source file not in the map (new, or no test runs it) adds nothing when the change has other files placed,
      since only the code calling it reaches it; on its own, the classes running its folder, else its parent's.
    - Scripts and styles under wwwroot, and a page or component not in the map (a page is reached by its address
      alone): the browser tests and every class that draws a page (MainLayout.razor).
    - Form snapshots: FormSnapshotTests. Classes named Postgres* (skipped on SQLite, so maybe not in the map) come
      with any change under Infrastructure.Postgres or Persistence.
    The SQLite suite also runs all of OpenBook.Domain.Tests (pure unit tests, seconds). When the classes picked
    are most of the suite anyway, it runs everything.

    Returns an object: Filter (empty = everything), Nothing (no tests needed), Reason, Classes.
#>
param(
    [string]$Base = 'master',
    [switch]$Postgres,
    [double]$AllAbove = 0.7,
    # These files (repository paths) instead of what git says changed: "which tests run this file?"
    [string[]]$Files
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$mapFile = Join-Path $root 'artifacts\test-map.json'

function Result([string]$Reason, [string[]]$Classes = @(), [switch]$Nothing) {
    $filter = ''
    if ($Classes.Count -gt 0) {
        $parts = @($Classes | Sort-Object -Unique | ForEach-Object { "FullyQualifiedName~$_." })
        if (-not $Postgres) { $parts += 'FullyQualifiedName~OpenBook.Domain.Tests.' }
        $filter = $parts -join '|'
    }
    [pscustomobject]@{ Filter = $filter; Nothing = [bool]$Nothing; Reason = $Reason; Classes = @($Classes | Sort-Object -Unique) }
}

if (-not (Test-Path $mapFile)) { return Result 'no test map yet (powershell -File tools/build-test-map.ps1)' }
$map = Get-Content $mapFile -Raw | ConvertFrom-Json
$classes = @($map.classes)
$byFile = @{}
foreach ($property in $map.files.PSObject.Properties) { $byFile[$property.Name] = @($property.Value) }
$age = (Get-Date) - [DateTimeOffset]::Parse($map.built).LocalDateTime

if ($Files) { $changed = @($Files) }
else {
    $mergeBase = (& git -C $root merge-base $Base HEAD 2>$null)
    if (-not $mergeBase) { return Result "no common commit with $Base" }
    $changed = @(& git -C $root diff --name-only $mergeBase.Trim()) + @(& git -C $root ls-files --others --exclude-standard)
}
$changed = @($changed | Where-Object { $_ } | ForEach-Object { $_ -replace '\\', '/' } | Sort-Object -Unique)
if ($changed.Count -eq 0) { return Result 'nothing changed' -Nothing }

$picked = New-Object 'System.Collections.Generic.HashSet[string]'
function Pick([object[]]$Indexes) { foreach ($i in $Indexes) { $null = $picked.Add($classes[[int]$i]) } }
function PickNamed([string]$Pattern) { foreach ($c in $classes) { if ($c -match $Pattern) { $null = $picked.Add($c) } } }
function MappedUnder([string]$Folder) {
    $found = @()
    foreach ($file in $byFile.Keys) { if ($file.StartsWith($Folder + '/', [StringComparison]::OrdinalIgnoreCase)) { $found += $byFile[$file] } }
    $found
}

$everything = @()
$unplaced = @()
$noTests = 0
foreach ($file in $changed) {
    $leaf = Split-Path $file -Leaf
    if (($file -match '\.(md|txt)$' -and $file -notmatch '^tests/') -or $file -match '^(docs|packaging|\.github)/' -or
        ($file -match '^tools/' -and $file -notmatch '^tools/pgtest\.ps1$') -or
        $leaf -in @('LICENSE', '.gitignore', '.gitattributes', '.editorconfig', 'Dockerfile', 'docker-compose.yml', '.dockerignore')) {
        $noTests++
        continue
    }
    # Migrations (not measured: every test runs them while its template is made) and build files reach everything.
    if ($file -match '^src/OpenBook\.Infrastructure(\.Postgres)?/(Persistence/)?Migrations/' -or
        $file -match '\.(csproj|props|targets|slnx?)$' -or $leaf -in @('global.json', 'dotnet-tools.json', 'NuGet.config') -or $file -eq 'tools/pgtest.ps1') {
        $everything += $file
        continue
    }
    if ($file -match '^src/OpenBook\.Infrastructure\.Postgres/|^src/OpenBook\.Infrastructure/Persistence/') { PickNamed '\.Postgres[A-Za-z]*Tests$' }

    if ($file -match '^tests/OpenBook\.Domain\.Tests/') {
        # The SQLite suite runs every Domain test; on PostgreSQL the domain code is covered through the map.
        if ($Postgres) { $noTests++ }
        continue
    }
    if ($file -match '^tests/OpenBook\.Integration\.Tests/Forms/Snapshots/') { PickNamed '\.FormSnapshotTests$'; continue }
    if ($file -match '^tests/OpenBook\.Integration\.Tests/.*\.cs$') {
        # A file of tests runs its own classes. Not through the map: xUnit runs every class's test data (MemberData,
        # attributes) while it finds the tests, so the map has such files reached by every class.
        $path = Join-Path $root $file
        if (-not (Test-Path $path)) { $noTests++; continue }   # deleted: its tests are gone with it
        $text = Get-Content $path -Raw
        $declared = @()
        if ($text -match '\[(Fact|Theory|SqliteFact|PostgresFact|BrowserFact)\b') {
            $namespace = if ($text -match '(?m)^\s*namespace\s+([\w.]+)') { $Matches[1] } else { 'OpenBook.Integration.Tests' }
            $declared = @([regex]::Matches($text, '(?m)^\s*(?:public\s+|internal\s+)?(?:sealed\s+|abstract\s+|static\s+)*(?:partial\s+)?class\s+(\w+)') |
                ForEach-Object { "$namespace.$($_.Groups[1].Value)" })
            # Helper classes beside the tests aren't test classes: keep the names the map knows, or that are new.
            $known = @($declared | Where-Object { $classes -contains $_ })
            if ($known.Count -gt 0) { $declared = $known }
        }
        if ($declared.Count -gt 0) { foreach ($d in $declared) { $null = $picked.Add($d) } }
        elseif ($byFile.ContainsKey($file)) { Pick $byFile[$file] }   # a helper: the classes that use it
        else { $everything += $file }
        continue
    }

    if ($byFile.ContainsKey($file)) { Pick $byFile[$file]; continue }
    # Scripts, styles and a new page reach every page drawn: the browser tests and every class that draws one
    # (MainLayout; App.razor is markup only, so coverage never records it). A page is reached by its address alone.
    if ($file -match '^src/OpenBook\.Web/wwwroot/' -or ($file -match '^src/OpenBook\.Web/Components/.*\.razor$')) {
        PickNamed '\.Browser\.'
        $pages = $byFile['src/OpenBook.Web/Components/Layout/MainLayout.razor']
        if ($pages) { Pick $pages } else { $everything += $file }
        continue
    }
    if ($file -match '^src/.*\.(cs|razor)$') {
        # New, or no test runs it: what reaches it is the code calling it, which is in the change too. Only when
        # nothing else in the change is placed do the classes running its folder stand in (below).
        $unplaced += $file
        continue
    }
    if ($file -match '^src/OpenBook\.Web/(Localization|Resources)/') {
        $found = @(MappedUnder 'src/OpenBook.Web/Localization')
        if ($found) { Pick $found } else { $everything += $file }
        continue
    }
    $everything += $file
}

if ($unplaced.Count -gt 0 -and $picked.Count -eq 0 -and $everything.Count -eq 0) {
    foreach ($file in $unplaced) {
        $folder = (Split-Path $file -Parent) -replace '\\', '/'
        $found = @()
        while (-not $found -and $folder -match '^src/[^/]+/.+') {
            $found = @(MappedUnder $folder)
            if (-not $found) { $folder = (Split-Path $folder -Parent) -replace '\\', '/' }
        }
        if ($found) { Pick $found } else { $everything += $file }
    }
}

$note = if ($age.TotalDays -ge 7) { (" (the test map is {0:N0} days old: rebuild it with tools/build-test-map.ps1)" -f $age.TotalDays) } else { '' }
if ($everything.Count -gt 0) {
    $shown = ($everything | Select-Object -First 3) -join ', '
    if ($everything.Count -gt 3) { $shown += ", and $($everything.Count - 3) more" }
    return Result "$($changed.Count) files; the map can't narrow $shown$note"
}
if ($picked.Count -eq 0) {
    return Result "$($changed.Count) files, none run by a test ($noTests need none)$note" -Nothing
}
if ($picked.Count -ge $AllAbove * $classes.Count) {
    return Result "$($changed.Count) files reach $($picked.Count) of $($classes.Count) classes$note"
}
Result "$($changed.Count) files reach $($picked.Count) of $($classes.Count) test classes$note" ([string[]]@($picked))
