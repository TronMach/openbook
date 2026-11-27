<#
.SYNOPSIS
    Installs OpenBook Server for Windows for real, checks it, updates it, uninstalls it, and brings a Desktop company in.

.DESCRIPTION
    Only for a computer that can be thrown away: CI (.github/workflows/installer.yml) or a virtual machine. It needs
    administrator rights, makes the service and the firewall rule, and deletes %ProgramData%\OpenBook between its two
    installs. So it refuses to start where OpenBook Server or its folder is already there, or the person has a company with
    OpenBook Desktop.

.EXAMPLE
    powershell -File tools/test-server-install.ps1 -Installer artifacts/desktop/OpenBook-Setup-1.2.0.exe
#>
param(
    [Parameter(Mandatory)][string]$Installer,
    [string]$Logs = (Join-Path ([IO.Path]::GetTempPath()) 'openbook-install-test')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Installer = (Resolve-Path $Installer).Path
$data = Join-Path $env:ProgramData 'OpenBook'
$program = Join-Path $env:ProgramFiles 'OpenBook Server'
$documents = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'OpenBook'
$desktopApp = Join-Path $env:LOCALAPPDATA 'OpenBook'
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\OpenBook Server.url'
$base = 'http://localhost:5190'
New-Item -ItemType Directory -Force $Logs | Out-Null

function Assert([bool]$condition, [string]$what) {
    if (-not $condition) { throw "FAILED: $what" }
    Write-Host "ok: $what"
}

function Get-Page([string]$path) { (Invoke-WebRequest "$base$path" -UseBasicParsing -TimeoutSec 60).Content }

function Invoke-Setup([string]$name, [string[]]$more = @(), [int]$exit = 0) {
    $log = Join-Path $Logs "$name.log"
    $p = Start-Process $Installer -ArgumentList (@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/ALLUSERS', "/LOG=`"$log`"") + @($more | Where-Object { $_ })) -PassThru
    # Windows PowerShell forgets the exit code unless the handle was asked for while it ran.
    $null = $p.Handle
    if (-not $p.WaitForExit(900000)) { $p.Kill(); throw "FAILED: the installer ($name) didn't finish in 15 minutes. $log" }
    if ($p.ExitCode -ne $exit) { Get-Content $log -Tail 40 | Write-Host }
    Assert ($p.ExitCode -eq $exit) "$name ended with code $exit"
    Select-String -Path $log -Pattern '(install|stop)-service|bring-desktop-company' | ForEach-Object { Write-Host "   $($_.Line)" }
}

function Remove-Program {
    $p = Start-Process (Join-Path $program 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru
    # The uninstaller goes on in a copy of itself.
    $null = $p.WaitForExit(300000)
    for ($i = 0; $i -lt 60 -and (Test-Path $program); $i++) { Start-Sleep 2 }
}

function Invoke-Uninstall {
    Remove-Program
    Assert (-not (Get-Service OpenBook -ErrorAction SilentlyContinue)) 'uninstalling takes the service away'
    Assert (-not (Get-NetFirewallRule -DisplayName 'OpenBook Server' -ErrorAction SilentlyContinue)) 'and the firewall rule'
    Assert (-not (Test-Path $program)) 'and the program'
    Assert (-not (Test-Path $shortcut)) 'and the Start menu shortcut'
    Assert (Test-Path (Join-Path $data 'openbook.db')) 'but not the books'
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this as administrator, on a computer that can be thrown away.'
}
foreach ($there in $data, $program, $documents, $desktopApp) {
    if (Test-Path $there) { throw "$there is already there: this only runs on a computer without OpenBook." }
}
if (Get-Service OpenBook -ErrorAction SilentlyContinue) { throw 'An OpenBook service is already there.' }

# A folder someone else made in ProgramData is never taken over: what they left in it would run as the service.
New-Item -ItemType Directory -Force $data | Out-Null
$acl = Get-Acl $data
$acl.SetOwner([Security.Principal.SecurityIdentifier]'S-1-5-32-545')
try { Set-Acl $data $acl; $planted = $true } catch { $planted = $false }
if ($planted) {
    Invoke-Setup 'planted' -exit 20
    Assert (-not (Get-Service OpenBook -ErrorAction SilentlyContinue)) 'a folder another person made is refused'
    Remove-Program
}
Remove-Item $data -Recurse -Force

# First install.
Invoke-Setup 'install'
$service = Get-CimInstance Win32_Service -Filter "Name='OpenBook'"
Assert ($service.State -eq 'Running') 'the service runs'
Assert ($service.StartName -eq 'NT SERVICE\OpenBook') 'as its own account'
Assert ($service.StartMode -eq 'Auto') 'and starts with Windows'
Assert ((Get-Page '/healthz') -match 'ok') 'it answers on this computer'
Assert ((Invoke-WebRequest "http://$($env:COMPUTERNAME):5190/healthz" -UseBasicParsing).StatusCode -eq 200) 'and by its name'
Assert (Test-Path $shortcut) 'the Start menu has it'
Assert ((Get-Content $shortcut -Raw) -match "URL=http://$($env:COMPUTERNAME):5190/") 'with the address others use'
Assert (-not (Test-Path (Join-Path $program 'OpenBook.Desktop.exe'))) "Desktop's launcher isn't installed"
Assert ((Get-Content (Join-Path $program 'openbook.install.json') -Raw) -match 'WindowsServer') 'the copy knows what it is'

$acl = Get-Acl $data
Assert $acl.AreAccessRulesProtected 'the folder inherits no permissions'
$who = @($acl.Access | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
Assert ($who.Count -eq 3 -and $who -contains 'NT SERVICE\OpenBook') "only administrators, Windows and the service may open it ($($who -join ', '))"
$fileAcl = Get-Acl (Join-Path $data 'openbook.db')
Assert (-not ($fileAcl.Access | Where-Object { $_.IdentityReference.Value -match 'Users|Everyone' })) 'nor the company file'

$rule = Get-NetFirewallRule -DisplayName 'OpenBook Server'
Assert (@($rule).Count -eq 1 -and "$($rule.Profile)" -notmatch 'Public|Any') "the firewall lets in home, office and domain networks only ($($rule.Profile))"
Assert (($rule | Get-NetFirewallPortFilter).LocalPort -eq '5190') 'on its port'

# The setup page, with the code only an administrator can read.
$codeFile = Join-Path $data 'setup-code.txt'
Assert (Test-Path $codeFile) 'the setup code is in its file'
$code = (Get-Content $codeFile -Raw).Trim()
$page = Invoke-WebRequest "$base/setup?code=$code" -UseBasicParsing -SessionVariable web
Assert ($page.Content -match [regex]::Escape("value=`"$code`"")) 'the setup page opens with the code filled in'
$token = [Net.WebUtility]::HtmlDecode([regex]::Match($page.Content, 'name="__RequestVerificationToken" value="([^"]+)"').Groups[1].Value)
$password = "test-$([Guid]::NewGuid().ToString('N'))"
$done = Invoke-WebRequest "$base/setup?code=$code" -Method Post -WebSession $web -UseBasicParsing -Body @{
    '_handler' = 'setup'; '__RequestVerificationToken' = $token; 'Input.SetupCode' = $code; 'Input.CompanyName' = 'Installed Co'
    'Input.BusinessType' = 'SoleProprietorship'; 'Input.HomeCurrency' = 'USD'; 'Input.FiscalYearStartMonth' = '1'
    'Input.Username' = 'owner'; 'Input.Password' = $password; 'Input.ConfirmPassword' = $password
}
Assert ($done.StatusCode -eq 200 -and $done.Content -notmatch 'Set up your company') 'a company is set up'
Assert (-not (Test-Path $codeFile)) 'and the code is gone'
$system = (Invoke-WebRequest "$base/settings/system" -WebSession $web -UseBasicParsing).Content
Assert ($system -match "http://$($env:COMPUTERNAME):5190") 'the System page gives the address for other computers'

$backups = Join-Path $data 'Backups'
for ($i = 0; $i -lt 60 -and -not (Get-ChildItem $backups -Filter 'openbook-scheduled-*' -ErrorAction SilentlyContinue); $i++) { Start-Sleep 3 }
Assert ([bool](Get-ChildItem $backups -Filter 'openbook-scheduled-*' -ErrorAction SilentlyContinue)) 'the first daily backup was made'

# Commands: as administrator, from anywhere; those that rewrite the books want it stopped.
Push-Location $env:SystemRoot
try {
    & (Join-Path $program 'OpenBook.Web.exe') verify-audit | Write-Host
    Assert ($LASTEXITCODE -eq 0) 'verify-audit runs while it does'
    $ErrorActionPreference = 'Continue'
    & (Join-Path $program 'OpenBook.Web.exe') restore-backup nothing 2>$null
    $restore = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Assert ($restore -eq 1) 'restore-backup waits for it to be stopped'
}
finally { Pop-Location }

# An update: the same installer again.
$before = (Get-Process -Id (Get-CimInstance Win32_Service -Filter "Name='OpenBook'").ProcessId).StartTime
Invoke-Setup 'update'
$after = (Get-Process -Id (Get-CimInstance Win32_Service -Filter "Name='OpenBook'").ProcessId).StartTime
Assert ($after -gt $before) 'the update restarted it'
Assert ((Get-Page '/login') -match 'Sign in') 'with its company'
Assert (@(Get-NetFirewallRule -DisplayName 'OpenBook Server').Count -eq 1) 'and one firewall rule'

# Windows starts it again when it crashes.
Stop-Process -Id (Get-CimInstance Win32_Service -Filter "Name='OpenBook'").ProcessId -Force
$up = $false
for ($i = 0; $i -lt 45 -and -not $up; $i++) { Start-Sleep 2; try { $up = (Get-Page '/healthz') -match 'ok' } catch { } }
Assert $up 'it comes back after a crash'

# A Desktop company to bring in, made with the installed program itself.
New-Item -ItemType Directory -Force $documents, (Join-Path $desktopApp 'keys') | Out-Null
& (Join-Path $program 'OpenBook.Web.exe') make-demo --OpenBook:Install=Server "--ConnectionStrings:OpenBook=Data Source=$(Join-Path $documents 'openbook.db')" --years 1 --invoices 5 --password $password | Select-Object -Last 2 | Write-Host
Assert (Test-Path (Join-Path $documents 'openbook.db')) 'there is a Desktop company'

Invoke-Uninstall
Remove-Item $data -Recurse -Force

Invoke-Setup 'install-with-desktop' @('/BRINGDESKTOP=1', '/PORT=5290')
$base = 'http://localhost:5290'
Assert (-not (Test-Path $codeFile)) "a company brought from Desktop needs no setup code"
Assert ((Get-Page '/login') -match 'Sign in') 'people sign in straight away, at the port asked for'
$page = Invoke-WebRequest "$base/login" -UseBasicParsing -SessionVariable web
$token = [Net.WebUtility]::HtmlDecode([regex]::Match($page.Content, 'name="__RequestVerificationToken" value="([^"]+)"').Groups[1].Value)
$home_ = Invoke-WebRequest "$base/login" -Method Post -WebSession $web -UseBasicParsing -Body @{ '_handler' = 'login'; '__RequestVerificationToken' = $token; 'Input.Username' = 'owner'; 'Input.Password' = $password }
Assert ($home_.Content -match 'Demo Trading Co') 'with the password they had'
Invoke-Uninstall

Write-Host 'Everything passed.'
