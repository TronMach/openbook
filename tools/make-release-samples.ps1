<#
.SYNOPSIS
    Makes a sample company file with each released OpenBook, for the restore drills (ReleaseSampleTests).

.DESCRIPTION
    For each release tag (default: every tag from v0.11.0, the first with make-demo, not yet sampled), checks the tag
    out into a worktree under artifacts\release-samples, builds it, and runs that release's own make-demo: a year of
    everyday work at a few invoices a month, entered through that release's services. The file is what a backup made by
    that release holds (a backup is a copy of the company file), gzipped into
    tests\OpenBook.Integration.Tests\Samples\Releases\<tag>.openbook.db.gz. Run it after each release and commit the
    new file, so every release's books are restored and checked by the tests from then on.

    Like run-tests.ps1, it keeps to -CpuPercent of the processors at below-normal priority, one release at a time.

.EXAMPLE
    powershell -File tools/make-release-samples.ps1                 # the releases not sampled yet
    powershell -File tools/make-release-samples.ps1 -Tags v0.14.0   # one release (made again if it's there)
#>
param(
    [string[]]$Tags,
    [int]$Invoices = 6,
    [ValidateRange(10, 100)][int]$CpuPercent = 85
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$samples = Join-Path $root 'tests\OpenBook.Integration.Tests\Samples\Releases'
$work = Join-Path $root 'artifacts\release-samples'
New-Item -ItemType Directory -Force $samples, $work | Out-Null

$cores = [Environment]::ProcessorCount
$usable = [Math]::Max(1, [Math]::Min([int][Math]::Floor($cores * $CpuPercent / 100.0), $(if ($cores -ge 4) { $cores - 1 } else { $cores })))
$self = [System.Diagnostics.Process]::GetCurrentProcess()
if ($usable -lt $cores) { $self.ProcessorAffinity = [IntPtr]([long][Math]::Pow(2, $usable) - 1) }
$self.PriorityClass = 'BelowNormal'

function Version-Of([string]$tag) { [Version]($tag.TrimStart('v')) }

if (-not $Tags) {
    $Tags = @(git -C $root tag --list 'v*' | Where-Object { (Version-Of $_) -ge [Version]'0.11.0' } |
        Where-Object { -not (Test-Path (Join-Path $samples "$_.openbook.db.gz")) } | Sort-Object { Version-Of $_ })
}
if ($Tags.Count -eq 0) { Write-Host 'Every release has a sample.'; exit 0 }

foreach ($tag in $Tags) {
    Write-Host "== $tag"
    $tree = Join-Path $work "src-$tag"
    if (Test-Path $tree) { git -C $root worktree remove --force $tree 2>$null | Out-Null; Remove-Item -Recurse -Force $tree -ErrorAction SilentlyContinue }
    git -C $root worktree add --detach $tree $tag | Out-Null
    try {
        & dotnet build (Join-Path $tree 'src\OpenBook.Web') -c Release -nologo -v q --disable-build-servers
        if ($LASTEXITCODE -ne 0) { throw "$tag didn't build." }
        $bin = Join-Path $tree 'src\OpenBook.Web\bin\Release\net10.0'
        $file = Join-Path $work "$tag.openbook.db"
        Remove-Item "$file*" -Force -ErrorAction SilentlyContinue
        $password = [Guid]::NewGuid().ToString('N')
        Push-Location $bin
        try {
            & dotnet OpenBook.Web.dll make-demo "--ConnectionStrings:OpenBook=Data Source=$file" --years 1 --invoices $Invoices --password $password |
                Select-Object -Last 2 | Where-Object { $_ -notmatch 'password' } | Write-Host
            if ($LASTEXITCODE -ne 0) { throw "$tag's make-demo failed." }
        }
        finally { Pop-Location }

        # A clean exit folds the write-ahead log into the file, so the file alone is the company, as a backup is.
        if (Test-Path "$file-wal") { throw "$tag left a write-ahead log beside the file: the sample would miss what's in it." }
        $target = Join-Path $samples "$tag.openbook.db.gz"
        Add-Type -AssemblyName System.IO.Compression
        $source = [IO.File]::OpenRead($file)
        try {
            $out = [IO.File]::Create($target)
            try {
                $gzip = New-Object IO.Compression.GZipStream($out, [IO.Compression.CompressionLevel]::Optimal)
                try { $source.CopyTo($gzip) } finally { $gzip.Dispose() }
            }
            finally { $out.Dispose() }
        }
        finally { $source.Dispose() }
        Write-Host ("{0}: {1:N0} KB" -f $target, ((Get-Item $target).Length / 1KB))
    }
    finally {
        git -C $root worktree remove --force $tree 2>$null | Out-Null
        Remove-Item -Recurse -Force $tree -ErrorAction SilentlyContinue
    }
}
