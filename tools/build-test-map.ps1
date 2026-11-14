<#
.SYNOPSIS
    Maps every source file to the integration test classes that run it, for run-tests.ps1 -Changed.

.DESCRIPTION
    Runs each integration test class on its own with coverage (coverlet, already a test dependency) on SQLite and
    writes artifacts\test-map.json: for each file under src and tests that a class ran at least one line of, the
    classes that did. The map is by file and nearly every file runs on SQLite too, so a PostgreSQL pass (-Provider
    Both or Postgres, on the test server of tools/pgtest.ps1) adds little for twice the time; changed-tests places
    files only PostgreSQL runs by their folder, and picks the Postgres* classes for any change to persistence.
    Migrations aren't measured (they were most of the time): every test runs them, so a change there runs everything.

    Coverlet rewrites the assemblies it measures, so each worker runs from its own copy of the test output in
    artifacts\test-map\work-<time> (removed afterwards). Workers keep to -CpuPercent of the processors at
    below-normal priority, like run-tests.ps1. Took 38 minutes on 2026-11-14 (145 classes, 6 at once); rebuild the
    map when a lot has changed (changed-tests says once it's a week old) or after adding test classes.

.EXAMPLE
    powershell -File tools/build-test-map.ps1
    powershell -File tools/build-test-map.ps1 -Only '\.InviteTests$'     # a trial: artifacts\test-map\trial.json
#>
param(
    [ValidateSet('Sqlite', 'Both', 'Postgres')][string]$Provider = 'Sqlite',
    [int]$Workers = 6,
    [ValidateRange(10, 100)][int]$CpuPercent = 85,
    [switch]$NoBuild,
    # Only the classes whose full name matches (a trial run); the map then goes to -Out, not the usual file.
    [string]$Only,
    [string]$Out
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tests\OpenBook.Integration.Tests'
$output = Join-Path $project 'bin\Debug\net10.0'
$dllName = 'OpenBook.Integration.Tests.dll'
# A folder of its own each time: an earlier run's can stay held open for a while after it ends.
Get-ChildItem (Join-Path $root 'artifacts\test-map') -Directory -Filter 'work*' -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -Recurse -Force $_.FullName -ErrorAction SilentlyContinue }
$work = Join-Path $root "artifacts\test-map\work-$(Get-Date -Format yyyyMMdd-HHmmss)"
$mapFile = if ($Out) { $Out } elseif ($Only) { Join-Path $root 'artifacts\test-map\trial.json' } else { Join-Path $root 'artifacts\test-map.json' }

$self = [System.Diagnostics.Process]::GetCurrentProcess()
$cores = [Environment]::ProcessorCount
$usable = [Math]::Max(1, [Math]::Min([int][Math]::Floor($cores * $CpuPercent / 100.0), $(if ($cores -ge 4) { $cores - 1 } else { $cores })))
if ($usable -lt $cores) { $self.ProcessorAffinity = [IntPtr]([long][Math]::Pow(2, $usable) - 1) }
$self.PriorityClass = 'BelowNormal'

if (-not $NoBuild) {
    & dotnet build $project -nologo -v q --disable-build-servers "-m:$usable"
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
}

$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$collector = Get-ChildItem (Join-Path $packages 'coverlet.collector') -Directory | Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending |
    ForEach-Object { Join-Path $_.FullName 'build\net10.0' } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $collector) { throw "coverlet.collector isn't restored under $packages." }

# Every test class, from the test names (Namespace.Class.Method, theories with their arguments after it).
$ErrorActionPreference = 'Continue'
$listed = & dotnet test (Join-Path $output $dllName) --list-tests 2>$null
$ErrorActionPreference = 'Stop'
$classes = @($listed | Where-Object { $_ -match '^\s+OpenBook\.Integration\.Tests\.' } | ForEach-Object {
        $name = ($_.Trim() -split '\(', 2)[0]
        $name.Substring(0, $name.LastIndexOf('.'))
    } | Sort-Object -Unique)
if ($Only) { $classes = @($classes | Where-Object { $_ -match $Only }) }
if ($classes.Count -eq 0) { throw 'No test classes found.' }
Write-Host "$($classes.Count) test classes; $Workers at a time on $usable of $cores processors."

# The longest last time go first, so the run doesn't end on one of them.
$timesFile = @((Join-Path $root 'artifacts\test-times\sqlite.json'), (Join-Path $PSScriptRoot 'test-times.json')) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($timesFile) {
    $times = @{}
    (Get-Content $timesFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $times[$_.Name] = [double]$_.Value }
    $classes = @($classes | Sort-Object { - [double]$times[$_] })
}

$settings = Join-Path $root 'artifacts\test-map\coverage.runsettings'
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
$null = New-Item -ItemType Directory -Force -Path $work
Set-Content -Path $settings -Encoding utf8 -Value @'
<RunSettings>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="XPlat code coverage">
        <Configuration>
          <Format>cobertura</Format>
          <Include>[OpenBook.*]*</Include>
          <!-- Migrations are most of both infrastructure assemblies (generated model snapshots) and took coverlet minutes
               per class to rewrite; every test runs them while its template is made, so changed-tests runs everything
               for a change there anyway. -->
          <Exclude>[OpenBook.Infrastructure]OpenBook.Infrastructure.Persistence.Migrations.*,[OpenBook.Infrastructure.Postgres]*</Exclude>
          <IncludeTestAssembly>true</IncludeTestAssembly>
          <SingleHit>true</SingleHit>
          <UseSourceLink>false</UseSourceLink>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
'@

# One copy of the test output per worker (inside the checkout, so the web tests still find src\OpenBook.Web).
for ($i = 1; $i -le $Workers; $i++) {
    $null = & robocopy $output (Join-Path $work "bin$i") /E /NFL /NDL /NJH /NJS /NP /R:2 /W:1
    if ($LASTEXITCODE -ge 8) { throw "Copying the test output failed (robocopy $LASTEXITCODE)." }
}

$files = @{}   # file -> set of class indexes
function Add-Coverage([string]$Directory, [int]$ClassIndex) {
    foreach ($report in Get-ChildItem $Directory -Recurse -Filter 'coverage.cobertura.xml') {
        $xml = New-Object System.Xml.XmlDocument
        $xml.Load($report.FullName)
        $sources = @($xml.coverage.sources.source | ForEach-Object { "$_" })
        foreach ($class in $xml.GetElementsByTagName('class')) {
            $hit = $false
            foreach ($line in $class.lines.line) { if ([int]$line.hits -gt 0) { $hit = $true; break } }
            if (-not $hit) { continue }
            $path = $class.filename
            if (-not [IO.Path]::IsPathRooted($path)) {
                $full = $sources | ForEach-Object { Join-Path $_ $path } | Where-Object { Test-Path $_ } | Select-Object -First 1
                if ($full) { $path = $full }
            }
            $full = [IO.Path]::GetFullPath($path)
            if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $relative = $full.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
            if (-not $files.ContainsKey($relative)) { $files[$relative] = New-Object 'System.Collections.Generic.HashSet[int]' }
            $null = $files[$relative].Add($ClassIndex)
        }
    }
}

$passes = switch ($Provider) { 'Both' { @('Sqlite', 'Postgres') } default { @($Provider) } }
$failedRuns = @()
foreach ($pass in $passes) {
    if ($pass -eq 'Postgres') {
        & (Join-Path $PSScriptRoot 'pgtest.ps1') start -CpuPercent $CpuPercent | Write-Host
        $env:OPENBOOK_TEST_POSTGRES = (Get-Content (Join-Path $root 'artifacts\pgtest\connection.txt') -Raw).Trim()
        $env:OPENBOOK_TEST_ALL_ON_POSTGRES = '1'
        $env:OPENBOOK_TEST_KEEP_LEFTOVERS = '1'
        $env:OPENBOOK_TEST_PG_COPY = 'FILE_COPY'
    }
    else {
        Remove-Item Env:OPENBOOK_TEST_POSTGRES, Env:OPENBOOK_TEST_ALL_ON_POSTGRES, Env:OPENBOOK_TEST_KEEP_LEFTOVERS, Env:OPENBOOK_TEST_PG_COPY -ErrorAction SilentlyContinue
    }

    $queue = New-Object System.Collections.Queue
    for ($c = 0; $c -lt $classes.Count; $c++) { $queue.Enqueue($c) }
    $running = @{}   # worker -> @{ Process; Class; Directory; Started }
    $passStarted = Get-Date
    $done = 0
    while ($queue.Count -gt 0 -or $running.Count -gt 0) {
        for ($w = 1; $w -le $Workers -and $queue.Count -gt 0; $w++) {
            if ($running.ContainsKey($w)) { continue }
            $c = $queue.Dequeue()
            $directory = Join-Path $work "$pass-$c"
            $arguments = @('test', "`"$(Join-Path $work "bin$w\$dllName")`"", '--filter', "FullyQualifiedName~$($classes[$c]).",
                '--settings', "`"$settings`"", '--collect', '"XPlat Code Coverage"', '--test-adapter-path', "`"$collector`"",
                '--results-directory', "`"$directory`"", '--', 'xUnit.MaxParallelThreads=1')
            $log = Join-Path $work "$pass-$c.log"
            $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
            $null = $process.Handle
            $process.PriorityClass = 'BelowNormal'
            $running[$w] = @{ Process = $process; Class = $c; Directory = $directory; Log = $log }
        }
        Start-Sleep -Milliseconds 500
        foreach ($w in @($running.Keys)) {
            $job = $running[$w]
            if (-not $job.Process.HasExited) { continue }
            $running.Remove($w)
            $done++
            # Exit code 1 is a failing test: its coverage still counts. Anything else, or no report, is noted.
            Add-Coverage $job.Directory $job.Class
            $reports = @(Get-ChildItem $job.Directory -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue)
            if ($reports.Count -eq 0) { $failedRuns += "$pass $($classes[$job.Class]) (exit $($job.Process.ExitCode), no coverage report; $($job.Log))" }
            elseif ($job.Process.ExitCode -gt 1) { $failedRuns += "$pass $($classes[$job.Class]) (exit $($job.Process.ExitCode); $($job.Log))" }
            Remove-Item -Recurse -Force $job.Directory -ErrorAction SilentlyContinue
            if ($done % 20 -eq 0) { Write-Host ("{0}: {1} of {2} classes, {3:N0} min" -f $pass, $done, $classes.Count, ((Get-Date) - $passStarted).TotalMinutes) }
        }
    }
    Write-Host ("{0} pass done in {1:N1} min." -f $pass, ((Get-Date) - $passStarted).TotalMinutes)
}

$map = [ordered]@{
    commit  = (& git -C $root rev-parse HEAD).Trim()
    built   = (Get-Date).ToString('o')
    passes  = $passes
    classes = $classes
    files   = [ordered]@{}
}
foreach ($file in ($files.Keys | Sort-Object)) { $map.files[$file] = @($files[$file] | Sort-Object) }
Set-Content -Path $mapFile -Encoding utf8 -Value ($map | ConvertTo-Json -Depth 4 -Compress)
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Write-Host "$($files.Count) files mapped to $($classes.Count) classes: $mapFile"
if ($failedRuns.Count -gt 0) {
    Write-Host "$($failedRuns.Count) class run(s) gave no coverage or didn't finish cleanly (their files may map to fewer classes):"
    $failedRuns | ForEach-Object { Write-Host "  $_" }
}
