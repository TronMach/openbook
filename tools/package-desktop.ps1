<#
.SYNOPSIS
    Builds the OpenBook Desktop installer for Windows (OpenBook-Setup-<version>.exe).

.DESCRIPTION
    Publishes the server (OpenBook.Web) and the launcher (OpenBook.Desktop) self-contained for win-x64 into one folder, adds
    openbook.install.json so that copy always runs as OpenBook Desktop (its commands too), and runs Inno Setup's compiler
    (ISCC.exe, free: winget install JRSoftware.InnoSetup) on packaging/windows/OpenBook.iss. Without Inno Setup, or with
    -SkipInstaller, it stops after publishing; the folder runs as it is (start OpenBook.Desktop.exe).

.EXAMPLE
    powershell -File tools/package-desktop.ps1 -Version 1.2.0
#>
param(
    [string]$Version = '1.0.0',
    [string]$Out = 'artifacts/desktop',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
$publish = Join-Path $out 'win-x64'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

foreach ($project in 'src/OpenBook.Web', 'src/OpenBook.Desktop') {
    Write-Host "Publishing $project..."
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained -o $publish -p:Version=$Version -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed." }
}

# Read at startup (Installation.InstallFile): this copy is OpenBook Desktop.
Set-Content -Path (Join-Path $publish 'openbook.install.json') -Value '{ "Install": "Desktop" }' -Encoding ascii
# Development settings don't belong in an install.
Remove-Item (Join-Path $publish 'appsettings.Development.json') -ErrorAction SilentlyContinue

if ($SkipInstaller) { Write-Host "Published to $publish"; return }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { Write-Warning "Inno Setup (ISCC.exe) not found: published to $publish without an installer."; return }

& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$publish" "/O$out" (Join-Path $root 'packaging\windows\OpenBook.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }
Write-Host "Installer: $(Join-Path $out "OpenBook-Setup-$Version.exe")"
