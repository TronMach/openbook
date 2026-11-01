<#
.SYNOPSIS
    Builds the QuickBooks Desktop export helper as one self-contained program (OpenBook.QuickBooksExport.exe).

.DESCRIPTION
    Publishes src/OpenBook.QuickBooksExport for win-x86 (QuickBooks' request processor is there for 32-bit programs on
    32- and 64-bit QuickBooks alike) as a single file that needs no .NET install, into -Out. Not trimmed: it talks to
    QuickBooks through late-bound COM. Not compressed and nothing extracted at start: antivirus heuristics took the
    compressed, self-extracting build for a packer and quarantined it. tools/package-desktop.ps1 runs this and ships
    the result in the installer's QuickBooks folder; for a server, copy the .exe into a QuickBooks folder beside
    OpenBook.Web.dll and the import page offers it for download.

.EXAMPLE
    powershell -File tools/package-qb-export.ps1 -Version 1.2.0
#>
param(
    [string]$Version = '1.0.0',
    [string]$Out = 'artifacts/quickbooks-export'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host 'Publishing src/OpenBook.QuickBooksExport...'
dotnet publish (Join-Path $root 'src/OpenBook.QuickBooksExport') -c Release -r win-x86 --self-contained -o $out -p:Version=$Version `
    -p:PublishSingleFile=true -p:DebugType=none -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish OpenBook.QuickBooksExport failed.' }
Write-Host "Helper: $(Join-Path $out 'OpenBook.QuickBooksExport.exe')"
