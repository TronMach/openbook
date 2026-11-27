; OpenBook for Windows. Built by tools/package-desktop.ps1, which publishes the app and passes:
;   /DAppVersion=1.0.0  /DSourceDir=<the self-contained publish folder>  /O<output folder>
; The first question is who will use it (Inno's install mode dialog, reworded below):
;   Just me (Inno's "current user"): OpenBook Desktop, without administrator rights, into %LOCALAPPDATA%\Programs\OpenBook.
;     The company file lives in Documents\OpenBook.
;   Several people (Inno's "all users", IsAdminInstallMode here): OpenBook Server for Windows, a Windows service other
;     computers on the network reach in their browsers, into Program Files\OpenBook Server. The company file, backups, keys
;     and settings live in %ProgramData%\OpenBook. The app's own commands do the work (WindowsServerCommands: install-service,
;     stop-service, remove-service; BringDesktopCommand).
; Neither folder of books is ever touched by installing, updating or uninstalling. Unattended: /VERYSILENT with /CURRENTUSER
; (also the default, which OpenBook Desktop's own updates rely on) or /ALLUSERS, and for a server /PORT=5190 and
; /BRINGDESKTOP=1 (the first install of a server only).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
; A test build (tools/package-desktop.ps1 -TestAppId) has its own AppId and name and no shortcuts, so trying an update never
; touches a real install.
#ifndef AppIdGuid
  #define AppIdGuid "9f6851af-8dd0-4c1e-a58b-d9943cc8e5e0"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<publish folder> (tools/package-desktop.ps1 does).
#endif

[Setup]
; Never change the AppId: it's how a newer setup finds and updates an existing install.
AppId={{{#AppIdGuid}}
#ifdef TestBuild
AppName=OpenBook update test
AppVerName=OpenBook update test {#AppVersion}
#else
AppName=OpenBook
AppVerName=OpenBook {#AppVersion}
#endif
AppVersion={#AppVersion}
; The setup's file details. Code signing (docs/admin/releasing.md) checks the product name and version.
VersionInfoProductName=OpenBook
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
AppPublisher=OpenBook
AppComments=Open-source bookkeeping for small businesses.
DefaultDirName={code:ProgramFolder}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Asked every time: one computer may have both, and a person with Desktop may want a server now.
UsePreviousPrivileges=no
; Documents and local app data are only read, to find the person's own OpenBook Desktop.
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=OpenBook-Setup-{#AppVersion}
SetupIconFile=openbook.ico
UninstallDisplayIcon={app}\openbook.ico
#ifdef TestBuild
UninstallDisplayName=OpenBook update test
#else
UninstallDisplayName={code:DisplayName}
#endif
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; OpenBook runs in the background: updating or uninstalling stops it first (Restart Manager).
CloseApplications=force
RestartApplications=no

[Messages]
PrivilegesRequiredOverrideTitle=Set Up OpenBook
PrivilegesRequiredOverrideInstruction=Who will use OpenBook?
PrivilegesRequiredOverrideText1=You can start with the first and move your company to the second later.
PrivilegesRequiredOverrideText2=You can start with the first and move your company to the second later.
PrivilegesRequiredOverrideAllUsers=&Several people on our network%nThis computer keeps the books and stays on; everyone opens OpenBook in their browser. Needs an administrator.
PrivilegesRequiredOverrideAllUsersRecommended=&Several people on our network%nThis computer keeps the books and stays on; everyone opens OpenBook in their browser. Needs an administrator.
PrivilegesRequiredOverrideCurrentUser=&Just me, on this computer
PrivilegesRequiredOverrideCurrentUserRecommended=&Just me, on this computer

[Tasks]
Name: "desktopicon"; Description: "Put an OpenBook icon on the desktop"; Flags: unchecked; Check: not IsAdminInstallMode

[Files]
Source: "{#SourceDir}\*"; Excludes: "OpenBook.Desktop.*,openbook.install*.json"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "openbook.ico"; DestDir: "{app}"; Flags: ignoreversion
; Read at startup (Installation.InstallFile): which of the two this copy is. The launcher and its tray icon are Desktop's.
Source: "{#SourceDir}\OpenBook.Desktop.*"; DestDir: "{app}"; Flags: ignoreversion; Check: not IsAdminInstallMode
Source: "{#SourceDir}\openbook.install.json"; DestDir: "{app}"; Flags: ignoreversion; Check: not IsAdminInstallMode
Source: "{#SourceDir}\openbook.install.server.json"; DestDir: "{app}"; DestName: "openbook.install.json"; Flags: ignoreversion; Check: IsAdminInstallMode

; No [InstallDelete] of "{app}\*": it runs before [Files] and a failed install can't bring those files back, which would
; leave no program at all. Old files a new version no longer has stay; .NET loads only what the new version lists.
; A file that must go gets its own named line under [InstallDelete].

#ifndef TestBuild
[Icons]
Name: "{autoprograms}\OpenBook"; Filename: "{app}\OpenBook.Desktop.exe"; WorkingDir: "{app}"; Comment: "Start OpenBook"; Check: not IsAdminInstallMode
Name: "{autodesktop}\OpenBook"; Filename: "{app}\OpenBook.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autoprograms}\OpenBook - Export from QuickBooks"; Filename: "{app}\QuickBooks\OpenBook.QuickBooksExport.exe"; WorkingDir: "{app}\QuickBooks"; Comment: "Save a QuickBooks Desktop company as files OpenBook imports"
#endif

[Run]
Filename: "{app}\OpenBook.Desktop.exe"; Description: "Start OpenBook now"; Flags: nowait postinstall skipifsilent; Check: not IsAdminInstallMode
; An update OpenBook installed itself (silently, with /RELAUNCH=1): start it again. It inherits the folders OpenBook ran with
; from the environment the updater set (UpdateService.InstallAsync).
Filename: "{app}\OpenBook.Desktop.exe"; Flags: nowait; Check: Relaunch
; A server: its page (the setup page, with its code, the first time), in the person's own browser rather than as administrator.
Filename: "{code:ServerAddress}"; Description: "{code:OpenDescription}"; Flags: shellexec nowait postinstall skipifsilent runasoriginaluser; Check: ServerRuns

[UninstallRun]
Filename: "{app}\OpenBook.Web.exe"; Parameters: "remove-service"; RunOnceId: "RemoveService"; Flags: runhidden; Check: IsAdminInstallMode

[UninstallDelete]
; Only the program folder. Documents\OpenBook (company file, backups), the keys in %LOCALAPPDATA%\OpenBook, and a server's
; %ProgramData%\OpenBook stay.
Type: filesandordirs; Name: "{app}"
Type: files; Name: "{commonprograms}\OpenBook Server.url"; Check: IsAdminInstallMode

[Code]
var
  DesktopPage: TInputOptionWizardPage;
  Address: String;
  ServerFailed: Boolean;

function ProgramFolder(Param: String): String;
begin
  if IsAdminInstallMode then
    Result := ExpandConstant('{commonpf}\OpenBook Server')
  else
    Result := ExpandConstant('{localappdata}\Programs\OpenBook');
end;

function DisplayName(Param: String): String;
begin
  if IsAdminInstallMode then
    Result := 'OpenBook Server'
  else
    Result := 'OpenBook';
end;

function Relaunch: Boolean;
begin
  Result := (not IsAdminInstallMode) and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

{ The server's address once it runs, with the setup code while it has no company; empty when it doesn't run. }
function ServerAddress(Param: String): String;
begin
  Result := Address;
end;

function ServerRuns: Boolean;
begin
  Result := IsAdminInstallMode and (Address <> '');
end;

function OpenDescription(Param: String): String;
begin
  if Pos('/setup?', Address) > 0 then
    Result := 'Set up your company now'
  else
    Result := 'Open OpenBook now';
end;

function DesktopCompany: String;
begin
  Result := ExpandConstant('{userdocs}\OpenBook\openbook.db');
end;

function DesktopKeys: String;
begin
  Result := ExpandConstant('{localappdata}\OpenBook\keys');
end;

{ A first install of a server on a computer where this person keeps a company with OpenBook Desktop. }
function MayBringDesktop: Boolean;
begin
  Result := IsAdminInstallMode
    and (not FileExists(ExpandConstant('{commonappdata}\OpenBook\openbook.db')))
    and FileExists(DesktopCompany) and DirExists(DesktopKeys);
end;

{ Someone with a server here who chose "Just me", most likely to update it. }
function InitializeSetup: Boolean;
begin
  Result := True;
  if (not IsAdminInstallMode) and (not WizardSilent) and FileExists(ExpandConstant('{commonpf}\OpenBook Server\OpenBook.Web.exe')) then
    Result := MsgBox('OpenBook Server is installed on this computer. To update it, run this setup again and choose "Several people on our network".' + #13#10#13#10
      + 'Install OpenBook Desktop as well, with a company of its own?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure InitializeWizard;
begin
  if not IsAdminInstallMode then
    exit;
  DesktopPage := CreateInputOptionPage(wpSelectTasks, 'Your company', 'This computer has a company kept by OpenBook Desktop.',
    'Bring it to the server, so everyone works on the same books? It''s copied, and the copy on this computer stays as it was; stop using that one afterwards.',
    True, False);
  DesktopPage.Add('Bring the company from OpenBook Desktop');
  DesktopPage.Add('Start with a new company');
  if WizardSilent and (ExpandConstant('{param:BRINGDESKTOP|0}') <> '1') then
    DesktopPage.SelectedValueIndex := 1
  else
    DesktopPage.SelectedValueIndex := 0;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (DesktopPage <> nil) and (PageID = DesktopPage.ID) and not MayBringDesktop;
end;

{ Runs one of the app's commands, hidden. True when it worked; Answer is the address, or why it didn't work. }
function RunCommand(Parameters: String; var Answer: String): Boolean;
var
  ResultFile: String;
  Lines: TArrayOfString;
  Code: Integer;
begin
  Result := False;
  ResultFile := ExpandConstant('{tmp}\result.txt');
  DeleteFile(ResultFile);
  if not Exec(ExpandConstant('{app}\OpenBook.Web.exe'), Parameters + ' --result "' + ResultFile + '"', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) then
    Answer := SysErrorMessage(Code)
  else if LoadStringsFromFile(ResultFile, Lines) and (GetArrayLength(Lines) > 1) then
  begin
    Answer := Lines[1];
    Result := (Lines[0] = 'ok') and (Code = 0);
  end
  else
    Answer := 'OpenBook stopped with code ' + IntToStr(Code) + '.';
  Log(Parameters + ': ' + Answer);
end;

{ An update: the service holds its files open, so it stops before they're replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Answer: String;
begin
  Result := '';
  if IsAdminInstallMode and FileExists(ExpandConstant('{app}\OpenBook.Web.exe')) then
    if not RunCommand('stop-service', Answer) then
      Result := 'OpenBook Server couldn''t be stopped: ' + Answer;
end;

procedure BringDesktop;
var
  Answer: String;
begin
  WizardForm.StatusLabel.Caption := 'Bringing your company from OpenBook Desktop...';
  while not RunCommand('bring-desktop-company --from "' + DesktopCompany + '" --keys "' + DesktopKeys + '"', Answer) do
    if SuppressibleMsgBox('Your company couldn''t be brought from OpenBook Desktop. ' + Answer + #13#10#13#10
      + 'Cancel goes on without it: the server starts with no company.', mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      ServerFailed := True;
      exit;
    end;
end;

procedure SetUpServer;
var
  Bring: Boolean;
  Port, Answer: String;
begin
  Bring := MayBringDesktop and (DesktopPage.SelectedValueIndex = 0);
  Port := ExpandConstant('{param:PORT|}');
  if Port <> '' then
    Port := ' --port ' + Port;

  WizardForm.StatusLabel.Caption := 'Setting up the OpenBook service...';
  if Bring then
  begin
    if not RunCommand('install-service --no-start' + Port, Answer) then
    begin
      ServerFailed := True;
      SuppressibleMsgBox('OpenBook Server couldn''t be set up. ' + Answer, mbCriticalError, MB_OK, IDOK);
      exit;
    end;
    BringDesktop;
  end;

  WizardForm.StatusLabel.Caption := 'Starting OpenBook...';
  if RunCommand('install-service' + Port, Answer) then
  begin
    Address := Answer;
    SetIniString('InternetShortcut', 'URL', Copy(Answer, 1, Pos('/', Copy(Answer, 9, Length(Answer))) + 8), ExpandConstant('{commonprograms}\OpenBook Server.url'));
    SetIniString('InternetShortcut', 'IconFile', ExpandConstant('{app}\openbook.ico'), ExpandConstant('{commonprograms}\OpenBook Server.url'));
    SetIniString('InternetShortcut', 'IconIndex', '0', ExpandConstant('{commonprograms}\OpenBook Server.url'));
  end
  else
  begin
    ServerFailed := True;
    SuppressibleMsgBox('OpenBook Server couldn''t be set up. ' + Answer, mbCriticalError, MB_OK, IDOK);
  end;
end;

{ The files are in place but the server isn't as asked: an unattended install must see that. }
function GetCustomSetupExitCode: Integer;
begin
  if ServerFailed then
    Result := 20
  else
    Result := 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and IsAdminInstallMode then
    SetUpServer;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and ServerRuns then
    WizardForm.FinishedLabel.Caption := 'OpenBook Server is running on this computer, and starts by itself whenever the computer does.' + #13#10#13#10
      + 'Other computers on your network open it in a browser at' + #13#10
      + Copy(Address, 1, Pos('/', Copy(Address, 9, Length(Address))) + 8) + #13#10#13#10
      + 'Keep this computer on and awake while people work. Daily backups are on from the start; Settings > Backup & restore says where they go.';
end;
