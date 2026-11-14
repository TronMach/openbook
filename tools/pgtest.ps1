<#
.SYNOPSIS
    A PostgreSQL server for the tests only, which doesn't wait for the disk: its own data folder, port 5433.

.DESCRIPTION
    Every integration test copies a template database and drops it afterwards, and every drop makes PostgreSQL write
    everything waiting in memory out to disk (a checkpoint). On a server that also flushes each write to the disk, a
    full run spent most of its 45 minutes waiting on those flushes (measured 2026-11-13: a template copy took 4.5 s
    and each drop up to 2.4 s on an idle machine). This server turns the flushing off (fsync, synchronous_commit,
    full_page_writes) and autovacuum, so copies and drops cost little. A crash can damage its data, so it never holds
    anything but test databases, and it's set up afresh whenever it isn't already running.

    It uses the PostgreSQL programs already installed (PGBIN, as on GitHub's runners, or the newest
    C:\Program Files\PostgreSQL\<version>\bin) and keeps everything in artifacts\pgtest, which the antivirus
    excludes along with the rest of the checkout. It listens on localhost only. The superuser and the test login
    (openbook_test, CREATEDB, not a superuser, like the login the tests use elsewhere) get new random passwords each
    time it's set up; the test login's connection string goes in artifacts\pgtest\connection.txt (run-tests.ps1
    reads it from there). Nothing secret is printed.

    Like the test runs, the server keeps to -CpuPercent of the processors at below-normal priority (its processes
    inherit both from this script). It keeps running after a run, for the next one; "stop" stops it.

.EXAMPLE
    powershell -File tools/pgtest.ps1               # start (setting it up if needed)
    powershell -File tools/pgtest.ps1 status
    powershell -File tools/pgtest.ps1 stop
#>
param(
    [ValidateSet('start', 'stop', 'status')][string]$Action = 'start',
    [int]$Port = 5433,
    [string]$Bin,
    [string]$Folder,
    [ValidateRange(10, 100)][int]$CpuPercent = 85
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Folder) { $Folder = Join-Path $root 'artifacts\pgtest' }
$data = Join-Path $Folder 'data'
$log = Join-Path $Folder 'server.log'
$connectionFile = Join-Path $Folder 'connection.txt'

if (-not $Bin) {
    if ($env:PGBIN -and (Test-Path (Join-Path $env:PGBIN 'pg_ctl.exe'))) { $Bin = $env:PGBIN }
    else {
        $found = Get-ChildItem 'C:\Program Files\PostgreSQL' -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'bin\pg_ctl.exe') } |
            Sort-Object { [double]($_.Name -replace '[^0-9.].*$', '') } -Descending | Select-Object -First 1
        if (-not $found) { throw 'No PostgreSQL programs found: install PostgreSQL, or pass -Bin <folder with pg_ctl.exe>.' }
        $Bin = Join-Path $found.FullName 'bin'
    }
}
$pgCtl = Join-Path $Bin 'pg_ctl.exe'
$initdb = Join-Path $Bin 'initdb.exe'
$psql = Join-Path $Bin 'psql.exe'

# Runs one of the PostgreSQL programs and waits for it alone (pg_ctl start leaves the server running behind it).
function Invoke-Pg([string]$Program, [string[]]$Arguments) {
    $quoted = $Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }
    $out = Join-Path $Folder 'last-command.out'
    $err = Join-Path $Folder 'last-command.err'
    $process = Start-Process -FilePath $Program -ArgumentList $quoted -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $null = $process.Handle
    $process.WaitForExit()
    $result = [pscustomobject]@{ Code = $process.ExitCode; Output = (Get-Content $out -Raw -ErrorAction SilentlyContinue); Error = (Get-Content $err -Raw -ErrorAction SilentlyContinue) }
    Remove-Item $out, $err -ErrorAction SilentlyContinue
    $result
}

function Test-Running {
    if (-not (Test-Path (Join-Path $data 'PG_VERSION'))) { return $false }
    (Invoke-Pg $pgCtl @('status', '-D', $data)).Code -eq 0
}

function New-Password {
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    ([Convert]::ToBase64String($bytes) -replace '[^A-Za-z0-9]', '')
}

# Two runs starting at once would both set the server up: one at a time.
$mutex = New-Object System.Threading.Mutex($false, 'Local\OpenBookTestPostgres')
$null = $mutex.WaitOne()
try {
    if (-not (Test-Path $Folder)) { $null = New-Item -ItemType Directory -Path $Folder }

    switch ($Action) {
        'status' {
            if (Test-Running) { "Running on port $Port ($data)." } else { 'Not running.' }
        }
        'stop' {
            if (Test-Running) {
                $result = Invoke-Pg $pgCtl @('stop', '-D', $data, '-m', 'fast', '-w')
                if ($result.Code -ne 0) { throw "pg_ctl stop failed: $($result.Error)" }
                'Stopped.'
            }
            else { 'Not running.' }
        }
        'start' {
            if ((Test-Running) -and (Test-Path $connectionFile)) {
                "Test server already running on port $Port."
                return
            }

            # The server's processes inherit this process's processors and priority.
            $self = [System.Diagnostics.Process]::GetCurrentProcess()
            $cores = [Environment]::ProcessorCount
            $usable = [Math]::Max(1, [Math]::Min([int][Math]::Floor($cores * $CpuPercent / 100.0), $(if ($cores -ge 4) { $cores - 1 } else { $cores })))
            if ($usable -lt $cores) { $self.ProcessorAffinity = [IntPtr]([long][Math]::Pow(2, $usable) - 1) }
            $self.PriorityClass = 'BelowNormal'

            # Not running: whatever is left can't be trusted without fsync, so set it up afresh.
            if (Test-Path $data) {
                if (Test-Running) { $null = Invoke-Pg $pgCtl @('stop', '-D', $data, '-m', 'immediate', '-w') }
                Remove-Item -Recurse -Force $data
            }

            $superPassword = New-Password
            $testPassword = New-Password
            $pwFile = Join-Path $Folder 'superuser.pw'
            Set-Content -Path $pwFile -Value $superPassword -Encoding ascii -NoNewline
            try {
                $result = Invoke-Pg $initdb @('-D', $data, '-U', 'postgres', '--auth=scram-sha-256', "--pwfile=$pwFile", '-E', 'UTF8', '--no-sync')
            }
            finally { Remove-Item $pwFile -ErrorAction SilentlyContinue }
            if ($result.Code -ne 0) { throw "initdb failed: $($result.Error)" }

            Add-Content -Path (Join-Path $data 'postgresql.conf') -Encoding ascii -Value @(
                '',
                '# OpenBook test server (tools/pgtest.ps1): test databases only, so nothing waits for the disk.',
                "port = $Port",
                "listen_addresses = 'localhost'",
                'fsync = off',
                'synchronous_commit = off',
                'full_page_writes = off',
                'autovacuum = off',
                'wal_level = minimal',
                'max_wal_senders = 0',
                'max_wal_size = 8GB',
                'checkpoint_timeout = 1h',
                'max_connections = 300',
                'shared_buffers = 512MB',
                'log_min_messages = warning'
            )

            if (Test-Path $log) { Remove-Item $log -Force }
            # Started through the shell, not with redirected output: a server started that way would hold on to every
            # handle this process can pass down, including the pipe of whoever reads this script's output (a shell,
            # GitHub Actions), which would then wait until the server stopped.
            $start = Start-Process -FilePath $pgCtl -ArgumentList @('start', '-D', "`"$data`"", '-l', "`"$log`"", '-w', '-t', '120') -WindowStyle Hidden -PassThru
            $null = $start.Handle
            $start.WaitForExit()
            if ($start.ExitCode -ne 0) { throw "The test server didn't start (pg_ctl exit code $($start.ExitCode)): see $log" }

            $env:PGPASSWORD = $superPassword
            try {
                $result = Invoke-Pg $psql @('-X', '-q', '-v', 'ON_ERROR_STOP=1', '-h', 'localhost', '-p', "$Port", '-U', 'postgres', '-d', 'postgres',
                    '-c', "CREATE ROLE openbook_test LOGIN CREATEDB PASSWORD '$testPassword'")
            }
            finally { Remove-Item Env:PGPASSWORD }
            if ($result.Code -ne 0) { throw "Couldn't create the test login: $($result.Error)" }

            $connection = "Host=localhost;Port=$Port;Username=openbook_test;Password=$testPassword;Database=postgres"
            Set-Content -Path $connectionFile -Value $connection -Encoding ascii -NoNewline
            "Test server set up and running on port $Port."
        }
    }
}
finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
