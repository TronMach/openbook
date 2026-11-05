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

.EXAMPLE
    powershell -File tools/run-tests.ps1                      # SQLite: every test project
    powershell -File tools/run-tests.ps1 -Postgres            # the integration suite on PostgreSQL (.env loaded)
    powershell -File tools/run-tests.ps1 -Postgres -EnvFile ..\..\..\.env    # from a worktree
    powershell -File tools/run-tests.ps1 -Filter "FullyQualifiedName~InviteTests" -Threads 2
#>
param(
    [switch]$Postgres,
    [int]$Threads = 0,
    [string]$Filter,
    [string]$EnvFile,
    [string]$Results = (Join-Path ([IO.Path]::GetTempPath()) 'openbook-test-results')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$name = if ($Postgres) { 'postgres' } else { 'sqlite' }
if ($Threads -le 0) { $Threads = if ($Postgres) { [Math]::Min(12, [Environment]::ProcessorCount) } else { [Math]::Max(2, [int]([Environment]::ProcessorCount / 4)) } }
$arguments = @('test')
if ($Postgres) {
    $arguments += 'tests/OpenBook.Integration.Tests'
    # .env (gitignored) holds the connection strings: KEY="value" lines, never printed.
    $envFile = if ($EnvFile) { $EnvFile } else { Join-Path $root '.env' }
    if (-not (Test-Path $envFile)) { throw "No .env beside the solution: PostgreSQL tests need OPENBOOK_TEST_POSTGRES." }
    foreach ($line in Get-Content $envFile) {
        # Values may be in single or double quotes (they hold ';'), as a shell's ". ./.env" reads them.
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$') {
            $value = $Matches[2]
            if ($value.Length -ge 2 -and ($value[0] -eq '"' -or $value[0] -eq "'") -and $value[-1] -eq $value[0]) { $value = $value.Substring(1, $value.Length - 2) }
            [Environment]::SetEnvironmentVariable($Matches[1], $value, 'Process')
        }
    }
    $env:OPENBOOK_TEST_ALL_ON_POSTGRES = '1'
}
if ($Filter) { $arguments += @('--filter', $Filter) }
$arguments += @('-nologo', '-v', 'q', '--logger', "trx;LogFilePrefix=$name", '--results-directory', $Results, '--', "xUnit.MaxParallelThreads=$Threads")

Write-Host "Running the $name tests, at most $Threads at once, at below-normal priority. Results: $Results\${name}_*.trx (one per test project)"
# Windows PowerShell joins the arguments with spaces, so quote any that contain one.
$quoted = $arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
$started = Get-Date
$process = Start-Process -FilePath 'dotnet' -ArgumentList $quoted -WorkingDirectory $root -NoNewWindow -PassThru
# Windows PowerShell only gives a -PassThru process an ExitCode if its handle was asked for while it ran.
$null = $process.Handle
$process.PriorityClass = 'BelowNormal'
$process.WaitForExit()
$code = $process.ExitCode

# The results files decide as well, so a failure can't be lost to how a shell reports exit codes.
$failed = 0
$ran = 0
$files = @(Get-ChildItem -Path $Results -Filter "${name}_*.trx" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $started })
foreach ($file in $files) {
    $document = New-Object System.Xml.XmlDocument
    $document.Load($file.FullName)
    $counters = $document.DocumentElement.ResultSummary.Counters
    $failed += [int]$counters.failed + [int]$counters.error + [int]$counters.timeout + [int]$counters.aborted
    $ran += [int]$counters.total
}
if ($failed -gt 0) { Write-Host "$failed test(s) failed."; exit 1 }
if ($ran -eq 0) { Write-Host "No tests ran."; exit 1 }
if ($null -eq $code -or $code -ne 0) { Write-Host "dotnet test ended with exit code '$code'."; exit 1 }
exit 0
