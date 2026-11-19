; Inno Setup script for RTA Sound Monitor. Build with installer\build-installer.ps1.
;
; Silent / scripted installs:  RtaSetup-x.y.z.exe /VERYSILENT /ROLE=Both|Node|Hub /RUNAS=signin|service [/TASKS="firewall,startup"]

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "RTA Sound Monitor"
#define ServiceName "RtaApp"
#define FirewallRule "RTA Sound Monitor"

[Setup]
AppId={{6C0B4E52-0F7E-4C6B-9F5E-2B7B7E5A3C11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=RtaSetup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\RtaTray.exe
CloseApplications=no
SetupLogging=yes

[Tasks]
Name: "firewall"; Description: "Allow RTA through Windows Firewall on private and domain networks (needed for the Hub, other nodes and NDI)"
Name: "startup"; Description: "Show the RTA status icon when anyone signs in"

[Dirs]
Name: "{commonappdata}\RtaApp"

[Files]
Source: "..\artifacts\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[INI]
Filename: "{group}\RTA dashboard.url"; Section: "InternetShortcut"; Key: "URL"; String: "http://localhost:5180/"
Filename: "{group}\RTA node page.url"; Section: "InternetShortcut"; Key: "URL"; String: "http://localhost:5180/node/"

[Icons]
Name: "{group}\RTA status icon"; Filename: "{app}\RtaTray.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commonstartup}\RTA status icon"; Filename: "{app}\RtaTray.exe"; Tasks: startup

[Run]
Filename: "{app}\RtaTray.exe"; Description: "Start RTA now"; Flags: nowait postinstall runasoriginaluser skipifsilent

[UninstallRun]
; Tray first (it restarts the app in sign-in mode), then a clean service stop (net waits), then delete.
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im RtaTray.exe"; Flags: runhidden waituntilterminated; RunOnceId: "KillTray"
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im RtaApp.exe"; Flags: runhidden waituntilterminated; RunOnceId: "KillApp"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; RunOnceId: "DeleteFirewall"

[UninstallDelete]
Type: files; Name: "{group}\RTA dashboard.url"
Type: files; Name: "{group}\RTA node page.url"

[Messages]
FinishedLabel=Setup has installed [name] on your computer.%n%nThe dashboard is at http://localhost:5180 on this PC (or http://<this PC's IP>:5180 from other devices). The first time you change a setting, you'll be asked to choose an admin password.%n%nFor NDI feeds, install NDI Tools from ndi.video. Your settings and logged data are kept in C:\ProgramData\RtaApp, even after uninstalling.

[Code]
var
  RolePage, RunPage: TInputOptionWizardPage;
  RunTouched: Boolean;

function Role: String;
begin
  case RolePage.SelectedValueIndex of
    1: Result := 'Node';
    2: Result := 'Hub';
  else
    Result := 'Both';
  end;
end;

function RunAs: String;
begin
  if RunPage.SelectedValueIndex = 1 then Result := 'service' else Result := 'signin';
end;

function ConfigPath: String;
begin
  Result := ExpandConstant('{commonappdata}\RtaApp\app.json');
end;

// Reads "Key": "value" from the existing app.json (good enough for the file we write ourselves).
function ExistingValue(Key: String): String;
var
  S: AnsiString;
  P: Integer;
  Rest: String;
begin
  Result := '';
  if not LoadStringFromFile(ConfigPath, S) then Exit;
  P := Pos('"' + Key + '"', String(S));
  if P = 0 then Exit;
  Rest := Copy(String(S), P + Length(Key) + 2, 200);
  P := Pos('"', Rest);
  if P = 0 then Exit;
  Rest := Copy(Rest, P + 1, 200);
  P := Pos('"', Rest);
  if P > 0 then Result := Copy(Rest, 1, P - 1);
end;

procedure InitializeWizard;
var
  R, A: String;
begin
  RolePage := CreateInputOptionPage(wpSelectDir, 'What does this PC do?',
    'Choose the role. You can change it later by running setup again.',
    'Each PC with RTA mics is a "node". One PC is the "Hub" that collects everything, logs it and serves the dashboard.',
    True, False);
  RolePage.Add('Mics and the Hub on this PC (one-PC setup, or the main PC)');
  RolePage.Add('Mics only: send readings to a Hub on another PC');
  RolePage.Add('Hub only: dashboard, logging and alerts, no mics on this PC');

  RunPage := CreateInputOptionPage(RolePage.ID, 'How should RTA start?',
    'Choose how the app runs on this PC.',
    'Audio interface drivers (ASIO) usually need someone signed in to Windows, so PCs with mics should start at sign-in. ' +
    'For an unattended booth PC, set Windows to sign in automatically. A Hub-only PC can run as a service that starts at boot.',
    True, False);
  RunPage.Add('When someone signs in to Windows, with a status icon (best for PCs with audio interfaces)');
  RunPage.Add('As a Windows service at boot, no sign-in needed (best for Hub-only PCs)');

  // Command line (/ROLE= /RUNAS=), then the previous install, then defaults.
  R := ExpandConstant('{param:ROLE|}');
  if R = '' then R := ExistingValue('Mode');
  if CompareText(R, 'Node') = 0 then RolePage.SelectedValueIndex := 1
  else if CompareText(R, 'Hub') = 0 then RolePage.SelectedValueIndex := 2
  else RolePage.SelectedValueIndex := 0;

  A := ExpandConstant('{param:RUNAS|}');
  if A = '' then A := ExistingValue('RunAs');
  RunTouched := A <> '';
  if CompareText(A, 'service') = 0 then RunPage.SelectedValueIndex := 1
  else if A <> '' then RunPage.SelectedValueIndex := 0
  else if RolePage.SelectedValueIndex = 2 then RunPage.SelectedValueIndex := 1
  else RunPage.SelectedValueIndex := 0;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // Suggest the start mode that suits the role, unless already chosen.
  if (CurPageID = RunPage.ID) and not RunTouched then
  begin
    if RolePage.SelectedValueIndex = 2 then RunPage.SelectedValueIndex := 1 else RunPage.SelectedValueIndex := 0;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = RunPage.ID then RunTouched := True;
  Result := True;
end;

procedure Exec2(Cmd, Params: String);
var
  Code: Integer;
begin
  Exec(Cmd, Params, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log(Format('%s %s -> %d', [Cmd, Params, Code]));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Stop anything from a previous install so files can be replaced. Order matters:
  // the tray would restart the app (sign-in mode), and killing the service's process
  // makes Windows restart it (recovery actions), so stop the service cleanly first.
  Exec2(ExpandConstant('{sys}\taskkill.exe'), '/f /im RtaTray.exe');
  Exec2(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');   // waits until stopped
  Exec2(ExpandConstant('{sys}\taskkill.exe'), '/f /im RtaApp.exe');
  Sleep(2000);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Urls, Json, Exe: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Exe := ExpandConstant('{app}\RtaApp.exe');

  // A node-only PC keeps its own page local; a Hub must be reachable from the network.
  // An address or port changed by hand in app.json is kept, unless the role changed.
  Urls := ExistingValue('Urls');
  if (Urls = '') or (CompareText(ExistingValue('Mode'), Role) <> 0) then
  begin
    if Role = 'Node' then Urls := 'http://localhost:5180' else Urls := 'http://0.0.0.0:5180';
  end;
  Json := '{' + #13#10 +
    '  "Mode": "' + Role + '",' + #13#10 +
    '  "RunAs": "' + RunAs + '",' + #13#10 +
    '  "Urls": "' + Urls + '"' + #13#10 + '}' + #13#10;
  SaveStringToFile(ConfigPath, Json, False);

  // Service: register (or remove when switching to sign-in). It was stopped in PrepareToInstall.
  Exec2(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}');
  Sleep(1000);
  if RunAs = 'service' then
  begin
    // Runs as the low-privilege LocalService account, which needs write access to the data folder.
    Exec2(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant('{commonappdata}\RtaApp') + '" /grant *S-1-5-19:(OI)(CI)M /T /Q');
    Exec2(ExpandConstant('{sys}\sc.exe'), 'create {#ServiceName} binPath= "\"' + Exe + '\"" start= delayed-auto DisplayName= "RTA Sound Monitor" obj= "NT AUTHORITY\LocalService"');
    Exec2(ExpandConstant('{sys}\sc.exe'), 'description {#ServiceName} "Sound level monitoring: RTA mics, Hub dashboard, logging and alerts."');
    Exec2(ExpandConstant('{sys}\sc.exe'), 'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/60000');
    // Also restart after a deliberate non-zero exit (restoring a backup restarts the app this way).
    Exec2(ExpandConstant('{sys}\sc.exe'), 'failureflag {#ServiceName} 1');
    Exec2(ExpandConstant('{sys}\sc.exe'), 'start {#ServiceName}');
  end
  else
    // Runs as whoever signs in: let local users write the data folder (settings, database, logs).
    Exec2(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant('{commonappdata}\RtaApp') + '" /grant *S-1-5-32-545:(OI)(CI)M /T /Q');

  Exec2(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="{#FirewallRule}"');
  if WizardIsTaskSelected('firewall') then
    Exec2(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="{#FirewallRule}" dir=in action=allow program="' + Exe + '" enable=yes profile=private,domain');
end;
