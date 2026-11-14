<#
.SYNOPSIS
    Runs the test suites gently: fewer tests at once and at below-normal priority, so the computer stays usable.

.DESCRIPTION
    xUnit runs as many tests at once as there are processor cores, and the PostgreSQL suite makes the database server
    work as hard, which can freeze the machine. This caps the tests running at once (-Threads, default a quarter of
    the cores; 12 for PostgreSQL, whose tests spend most of their time waiting on the database server) and starts the
    run at below-normal priority (the test host inherits it). A trx results file is saved per test
    project, so failures are named. Exits 1 if any test failed, going by the results files as well as dotnet's exit
    code, and if no tests ran at all.

    The whole run (the build, the test host and what it starts, such as the headless browser) is also kept to
    -CpuPercent of the processors (default 85, so 20 of 24 threads stay busy at most and the last cores are left
    alone), and the default caps above are lowered by the same share (decided 2026-11-13, after a blue screen at the
    end of a full run). Two runs at once share the same processors, so together they stay within it too.

    -Postgres runs on the test-only server of tools/pgtest.ps1 (-Server Fast, the default since 2026-11-13: port
    5433, nothing waits for the disk, started here within the same processors and priority, and left running for
    the next run). Copying and dropping a database per test on an ordinary server, which flushes every write, took
    most of a full run's 45 minutes. -Server Env uses OPENBOOK_TEST_POSTGRES from .env or the environment instead.

    After each run the time each test class took goes in artifacts\test-times\<suite>.json, and the next run starts
    the slowest classes first (OPENBOOK_TEST_TIMES, read by SlowestFirst in the integration tests); without that file
    it uses tools\test-times.json, kept in the repository for CI.

    -Changed runs only the test classes that run code changed since -Base (default master: committed, uncommitted
    and new files), going by the map tools/build-test-map.ps1 makes; see tools/changed-tests.ps1 for the rules. It
    runs everything when the change is too wide or the map can't tell. Run both full suites before pushing anyway.

.EXAMPLE
    powershell -File tools/run-tests.ps1                      # SQLite: every test project
    powershell -File tools/run-tests.ps1 -Postgres            # the integration suite on PostgreSQL (.env loaded)
    powershell -File tools/run-tests.ps1 -Postgres -EnvFile ..\..\..\.env    # from a worktree
    powershell -File tools/run-tests.ps1 -Filter "FullyQualifiedName~InviteTests" -Threads 2
    powershell -File tools/run-tests.ps1 -Postgres -Changed  # only the classes that run what changed since master
    dotnet build; then both suites at once, neither building (two builds at once lock each other's files):
    powershell -File tools/run-tests.ps1 -NoBuild    and, in a second window,    powershell -File tools/run-tests.ps1 -Postgres -NoBuild
#>
param(
    [switch]$Postgres,
    [switch]$NoBuild,
    [int]$Threads = 0,
    [string]$Filter,
    [string]$EnvFile,
    [ValidateSet('Fast', 'Env')][string]$Server = 'Fast',
    [switch]$Changed,
    [string]$Base = 'master',
    [ValidateRange(10, 100)][int]$CpuPercent = 85,
    [string]$Results = (Join-Path ([IO.Path]::GetTempPath()) 'openbook-test-results')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$name = if ($Postgres) { 'postgres' } else { 'sqlite' }
$cores = [Environment]::ProcessorCount
$share = $CpuPercent / 100.0
# Processors the run may use: the first ones, leaving the rest free (at least one used, and one free when there are 4+).
$usable = [Math]::Max(1, [Math]::Min([int][Math]::Floor($cores * $share), $(if ($cores -ge 4) { $cores - 1 } else { $cores })))
if ($Threads -le 0) {
    $full = if ($Postgres) { [Math]::Min(12, $cores) } else { [Math]::Max(2, [int]($cores / 4)) }
    $Threads = [Math]::Max(2, [int][Math]::Floor($full * $share))
}
# Set on this process first: what it starts (the build, the test host, the test server) inherits both.
$self = [System.Diagnostics.Process]::GetCurrentProcess()
if ($usable -lt $cores) { $self.ProcessorAffinity = [IntPtr]([long][Math]::Pow(2, $usable) - 1) }
$self.PriorityClass = 'BelowNormal'

$arguments = @('test')
if ($Postgres) {
    $arguments += 'tests/OpenBook.Integration.Tests'
    # .env (gitignored) holds the connection strings: KEY="value" lines, never printed.
    # Without one, the variables may already be set (the CI workflow sets them).
    $envFile = if ($EnvFile) { $EnvFile } else { Join-Path $root '.env' }
    $lines = @()
    if (Test-Path $envFile) { $lines = Get-Content $envFile }
    elseif ($Server -eq 'Env' -and -not $env:OPENBOOK_TEST_POSTGRES) { throw "No .env beside the solution and OPENBOOK_TEST_POSTGRES isn't set: -Server Env needs it." }
    foreach ($line in $lines) {
        # Values may be in single or double quotes (they hold ';'), as a shell's ". ./.env" reads them.
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$') {
            $value = $Matches[2]
            if ($value.Length -ge 2 -and ($value[0] -eq '"' -or $value[0] -eq "'") -and $value[-1] -eq $value[0]) { $value = $value.Substring(1, $value.Length - 2) }
            [Environment]::SetEnvironmentVariable($Matches[1], $value, 'Process')
        }
    }
    if ($Server -eq 'Fast') {
        & (Join-Path $PSScriptRoot 'pgtest.ps1') start -CpuPercent $CpuPercent | Write-Host
        $env:OPENBOOK_TEST_POSTGRES = (Get-Content (Join-Path $root 'artifacts\pgtest\connection.txt') -Raw).Trim()
        # Copying the template's files beats writing every block to the log (0.46 s a copy against 1.23 s) once
        # checkpoints cost nothing; on an ordinary server it's the other way round (LedgerDatabase.CopyStrategy).
        $env:OPENBOOK_TEST_PG_COPY = 'FILE_COPY'
    }
    $env:OPENBOOK_TEST_ALL_ON_POSTGRES = '1'
}

# Slowest classes first: this computer's times from earlier runs, else the repository's.
$timesFile = Join-Path $root "artifacts\test-times\$name.json"
$env:OPENBOOK_TEST_TIMES = if (Test-Path $timesFile) { $timesFile } else { Join-Path $PSScriptRoot 'test-times.json' }

if ($Changed) {
    $selection = & (Join-Path $PSScriptRoot 'changed-tests.ps1') -Base $Base -Postgres:$Postgres
    if ($selection.Filter) {
        Write-Host "Changed since ${Base}: $($selection.Reason)"
        $Filter = if ($Filter) { "($($selection.Filter))&($Filter)" } else { $selection.Filter }
    }
    elseif ($selection.Nothing) { Write-Host "Changed since ${Base}: $($selection.Reason). No tests to run."; exit 0 }
    else { Write-Host "Changed since ${Base}: $($selection.Reason). Running everything." }
}
if ($Filter) { $arguments += @('--filter', $Filter) }
if ($NoBuild) { $arguments += '--no-build' }
# A build server started earlier (by another build) wouldn't keep to the processors below, so this run's build uses none.
if (-not $NoBuild) { $arguments += '--disable-build-servers' }
$arguments += @('-nologo', '-v', 'q', '--logger', "trx;LogFilePrefix=$name", '--results-directory', $Results, '--', "xUnit.MaxParallelThreads=$Threads")

Write-Host "Running the $name tests, at most $Threads at once, on $usable of $cores processors, at below-normal priority. Results: $Results\${name}_*.trx (one per test project)"
# Windows PowerShell joins the arguments with spaces, so quote any that contain one.
$quoted = $arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
$started = Get-Date
$process = Start-Process -FilePath 'dotnet' -ArgumentList $quoted -WorkingDirectory $root -NoNewWindow -PassThru
# Windows PowerShell only gives a -PassThru process an ExitCode if its handle was asked for while it ran.
$null = $process.Handle
$process.PriorityClass = 'BelowNormal'
# The build's worker processes and the test host set their own priority back to normal (the processors limit holds),
# so every few seconds anything this run started is put back to below normal while it lasts.
$lowered = @{}
while (-not $process.WaitForExit(3000)) {
    try {
        $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId -ErrorAction Stop)
        $children = @{}
        foreach ($p in $all) { $children[[int]$p.ParentProcessId] += @([int]$p.ProcessId) }
        $queue = New-Object System.Collections.Queue
        $queue.Enqueue($process.Id)
        while ($queue.Count -gt 0) {
            foreach ($id in @($children[[int]$queue.Dequeue()])) {
                if ($null -eq $id -or $id -eq 0) { continue }
                $queue.Enqueue($id)
                if ($lowered.ContainsKey($id)) { continue }
                try {
                    $child = [System.Diagnostics.Process]::GetProcessById($id)
                    if ($child.PriorityClass -eq 'Normal') { $child.PriorityClass = 'BelowNormal' }
                    $lowered[$id] = $true
                }
                catch { }
            }
        }
    }
    catch { }
}
$code = $process.ExitCode

# The results files decide as well, so a failure can't be lost to how a shell reports exit codes.
$failed = 0
$ran = 0
$failures = @()
$classTimes = @{}
$files = @(Get-ChildItem -Path $Results -Filter "${name}_*.trx" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $started })
foreach ($file in $files) {
    $document = New-Object System.Xml.XmlDocument
    $document.Load($file.FullName)
    $counters = $document.DocumentElement.ResultSummary.Counters
    $failed += [int]$counters.failed + [int]$counters.error + [int]$counters.timeout + [int]$counters.aborted
    $ran += [int]$counters.total
    $classOf = @{}
    foreach ($test in $document.GetElementsByTagName('UnitTest')) { $classOf[$test.id] = $test.TestMethod.className }
    foreach ($result in $document.GetElementsByTagName('UnitTestResult')) {
        # From start to end, which includes each test's own database and app, not just its body.
        if ($result.startTime -and $result.endTime -and $classOf[$result.testId]) {
            $classTimes[$classOf[$result.testId]] += ([DateTimeOffset]::Parse($result.endTime) - [DateTimeOffset]::Parse($result.startTime)).TotalSeconds
        }
        if ($result.outcome -ne 'Passed' -and $result.outcome -ne 'NotExecuted') {
            $message = $result.GetElementsByTagName('Message') | Select-Object -First 1
            $first = if ($message) { ($message.InnerText -split "`r?`n")[0] } else { $result.outcome }
            if ($first.Length -gt 300) { $first = $first.Substring(0, 300) + '...' }
            $failures += [pscustomobject]@{ Name = $result.testName; Message = $first }
        }
    }
}

# This run's class times replace those of the same classes from earlier runs (a filtered run keeps the rest).
if ($classTimes.Count -gt 0) {
    try {
        $times = [ordered]@{}
        if (Test-Path $timesFile) {
            (Get-Content $timesFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $times[$_.Name] = [double]$_.Value }
        }
        foreach ($key in $classTimes.Keys) { $times[$key] = [Math]::Round($classTimes[$key], 1) }
        $sorted = [ordered]@{}
        foreach ($key in ($times.Keys | Sort-Object)) { $sorted[$key] = $times[$key] }
        $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $timesFile)
        Set-Content -Path $timesFile -Value ($sorted | ConvertTo-Json) -Encoding utf8
    }
    catch { Write-Host "Couldn't save the class times ($timesFile): $($_.Exception.Message)" }
}
if ($failed -gt 0) {
    Write-Host "$failed test(s) failed:"
    foreach ($failure in $failures) { Write-Host "  $($failure.Name)"; Write-Host "      $($failure.Message)" }
    # On GitHub Actions, the run's summary page lists them too.
    if ($env:GITHUB_STEP_SUMMARY) {
        $summary = @("### $failed $name test(s) failed", '')
        $summary += $failures | ForEach-Object { "- ``$($_.Name)``: $($_.Message -replace '\|', '\|')" }
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8
    }
    exit 1
}
if ($ran -eq 0) { Write-Host "No tests ran."; exit 1 }
if ($null -eq $code -or $code -ne 0) { Write-Host "dotnet test ended with exit code '$code'."; exit 1 }
exit 0
