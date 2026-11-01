; OpenBook Desktop for Windows. Built by tools/package-desktop.ps1, which publishes the app and passes:
;   /DAppVersion=1.0.0  /DSourceDir=<the self-contained publish folder>  /O<output folder>
; Installs for the current user only (no administrator rights) into %LOCALAPPDATA%\Programs\OpenBook.
; The company file lives in Documents\OpenBook and is never touched by installing, updating or uninstalling.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<publish folder> (tools/package-desktop.ps1 does).
#endif

[Setup]
; Never change the AppId: it's how a newer setup finds and updates an existing install.
AppId={{9f6851af-8dd0-4c1e-a58b-d9943cc8e5e0}
AppName=OpenBook
AppVerName=OpenBook {#AppVersion}
AppVersion={#AppVersion}
AppPublisher=OpenBook
AppComments=Open-source bookkeeping for small businesses.
DefaultDirName={localappdata}\Programs\OpenBook
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=OpenBook-Setup-{#AppVersion}
SetupIconFile=openbook.ico
UninstallDisplayIcon={app}\OpenBook.Desktop.exe
UninstallDisplayName=OpenBook
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; OpenBook runs in the background: updating or uninstalling stops it first (Restart Manager).
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Put an OpenBook icon on the desktop"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Files of an older version that this one no longer has.
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{autoprograms}\OpenBook"; Filename: "{app}\OpenBook.Desktop.exe"; WorkingDir: "{app}"; Comment: "Start OpenBook"
Name: "{autodesktop}\OpenBook"; Filename: "{app}\OpenBook.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autoprograms}\OpenBook - Export from QuickBooks"; Filename: "{app}\QuickBooks\OpenBook.QuickBooksExport.exe"; WorkingDir: "{app}\QuickBooks"; Comment: "Save a QuickBooks Desktop company as files OpenBook imports"

[Run]
Filename: "{app}\OpenBook.Desktop.exe"; Description: "Start OpenBook now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only the program folder. Documents\OpenBook (company file, backups) and the keys in %LOCALAPPDATA%\OpenBook stay.
Type: filesandordirs; Name: "{app}"
