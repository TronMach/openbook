<#
.SYNOPSIS
    Writes the package list (a CycloneDX 1.5 software bill of materials) of a published OpenBook Desktop.

.DESCRIPTION
    Reads every *.deps.json in the publish folder (the app, the desktop shell and the QuickBooks export helper), so the
    list is what the installer actually holds: each NuGet package with its version and the licence expression from its
    .nuspec, the .NET runtime packs it was published with, and the fonts shipped for printed forms. OpenBook's own
    projects are the subject of the list, not entries in it. The release workflow attaches the file to each release.
    Needs the packages in the NuGet cache (restore or publish first).

.EXAMPLE
    powershell -File tools/make-sbom.ps1 -Version 1.2.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Publish = 'artifacts/desktop/win-x64',
    [string]$Out
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($Publish)) { $Publish = Join-Path $root $Publish }
if (-not $Out) { $Out = Join-Path $root "artifacts/desktop/OpenBook-$Version.cdx.json" }
elseif (-not [IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $root $Out }
if (-not (Test-Path $Publish)) { throw "No publish folder at ${Publish}: run tools/package-desktop.ps1 first." }

$cache = $env:NUGET_PACKAGES
if (-not $cache) {
    $line = (& dotnet nuget locals global-packages --list) -join ''
    $cache = ($line -replace '^\s*global-packages:\s*', '').Trim()
}

function Get-Licence([string]$id, [string]$version) {
    $nuspec = Join-Path $cache (Join-Path $id.ToLowerInvariant() (Join-Path $version.ToLowerInvariant() "$($id.ToLowerInvariant()).nuspec"))
    if (-not (Test-Path $nuspec)) { return $null }
    $document = New-Object System.Xml.XmlDocument
    $document.Load($nuspec)
    $license = $document.GetElementsByTagName('license') | Select-Object -First 1
    if ($license -and $license.GetAttribute('type') -eq 'expression') { return @{ expression = $license.InnerText.Trim() } }
    $url = $document.GetElementsByTagName('licenseUrl') | Select-Object -First 1
    if ($url) { return @{ license = @{ url = $url.InnerText.Trim() } } }
    return $null
}

$components = [ordered]@{}
$depsFiles = Get-ChildItem -Path $Publish -Filter '*.deps.json' -Recurse
if ($depsFiles.Count -eq 0) { throw "No .deps.json files in $Publish." }
foreach ($file in $depsFiles) {
    $deps = Get-Content $file.FullName -Raw | ConvertFrom-Json
    foreach ($library in $deps.libraries.PSObject.Properties) {
        $id, $libraryVersion = $library.Name -split '/', 2
        switch ($library.Value.type) {
            'package' {
                $ref = "pkg:nuget/$id@$libraryVersion"
                if ($components.Contains($ref)) { continue }
                $component = [ordered]@{ type = 'library'; 'bom-ref' = $ref; name = $id; version = $libraryVersion; purl = $ref }
                $licence = Get-Licence $id $libraryVersion
                if ($licence) { $component.licenses = @($licence) }
                $components[$ref] = $component
            }
            'runtimepack' {
                # runtimepack.Microsoft.NETCore.App.Runtime.win-x64: the .NET runtime itself, published with the app.
                $pack = $id -replace '^runtimepack\.', ''
                $ref = "pkg:nuget/$pack@$libraryVersion"
                if ($components.Contains($ref)) { continue }
                $components[$ref] = [ordered]@{
                    type = 'framework'; 'bom-ref' = $ref; name = $pack; version = $libraryVersion; purl = $ref
                    licenses = @(@{ expression = 'MIT' })
                }
            }
        }
    }
}

$fonts = Join-Path $Publish 'Forms/Fonts/fonts.json'
if (Test-Path $fonts) {
    foreach ($font in (Get-Content $fonts -Raw | ConvertFrom-Json)) {
        $ref = "font:$($font.key)"
        $components[$ref] = [ordered]@{
            type = 'file'; 'bom-ref' = $ref; name = "$($font.family) (font for printed forms)"
            licenses = @(@{ expression = 'OFL-1.1' })
        }
    }
}

$bom = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.5'
    serialNumber = "urn:uuid:$([guid]::NewGuid())"
    version = 1
    metadata = [ordered]@{
        timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        tools = [ordered]@{ components = @([ordered]@{ type = 'application'; name = 'tools/make-sbom.ps1' }) }
        component = [ordered]@{
            type = 'application'; 'bom-ref' = "openbook@$Version"; name = 'OpenBook Desktop'; version = $Version
            licenses = @(@{ expression = 'AGPL-3.0-or-later' })
            externalReferences = @(@{ type = 'vcs'; url = 'https://github.com/TronMach/openbook' })
        }
    }
    components = @($components.Values | Sort-Object { $_.name }, { $_.version })
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
# UTF-8 without a byte order mark, which some readers of JSON refuse.
[IO.File]::WriteAllText($Out, ($bom | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding $false))
$licensed = @($components.Values | Where-Object { $_.licenses }).Count
Write-Host "Package list: $($components.Count) components ($licensed with a licence) in $Out"
