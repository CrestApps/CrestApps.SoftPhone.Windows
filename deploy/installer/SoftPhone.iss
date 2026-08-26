; Inno Setup script for Soft Phone — a download-and-run installer that also collects the
; tenant domain and preferences during setup, so the app is configured on first launch.
;
; Per-user install (no admin prompt), like VS Code/Slack. Compile with Inno Setup 6:
;   ISCC.exe /DAppVersion=0.1.0 /DPayloadDir="<portable build folder>" deploy\installer\SoftPhone.iss
; PayloadDir must contain CrestApps.SoftPhone.exe and Assets\.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\..\artifacts\portable\SoftPhone"
#endif

#define AppName "Soft Phone"
#define AppPublisher "CrestApps"
#define AppExe "CrestApps.SoftPhone.exe"

[Setup]
AppId={{9E1B4C2A-3D5F-4A6B-8C7D-0E1F2A3B4C5D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://crestapps.com
AppCopyright=© CrestApps. All rights reserved.
; File-properties (Details tab) version info for the generated setup.exe.
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription=Soft Phone Setup
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoCopyright=© CrestApps. All rights reserved.
DefaultDirName={localappdata}\Programs\Soft Phone
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\..\artifacts\installer
OutputBaseFilename=SoftPhone-Setup-v{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\..\src\SoftPhone.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Files]
Source: "{#PayloadDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch Soft Phone"; Flags: nowait postinstall skipifsilent

[Code]
var
  DomainPage: TInputQueryWizardPage;
  PrefsPage: TInputOptionWizardPage;

procedure InitializeWizard;
begin
  DomainPage := CreateInputQueryPage(wpWelcome,
    'Tenant domain',
    'Connect to your organization''s soft phone',
    'Enter your soft phone domain (for example, phone.example.com). You can change this later in Settings.');
  DomainPage.Add('Tenant domain:', False);
  { Pre-fill from /DOMAIN=... so the installer can also run silently (e.g. via Intune). }
  DomainPage.Values[0] := ExpandConstant('{param:DOMAIN|}');

  PrefsPage := CreateInputOptionPage(DomainPage.ID,
    'Preferences',
    'Choose your options',
    'These can all be changed later in Settings.', False, False);
  PrefsPage.Add('Start Soft Phone when I sign in to Windows');
  PrefsPage.Add('Play a ringtone for incoming calls');
  PrefsPage.Add('Keep the phone window always on top');
  PrefsPage.Values[0] := True;
  PrefsPage.Values[1] := True;
  PrefsPage.Values[2] := False;
end;

{ Basic domain validation: non-empty and looks like a host (has a dot, no spaces). }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  d: String;
begin
  Result := True;
  if CurPageID = DomainPage.ID then
  begin
    d := Trim(DomainPage.Values[0]);
    if (d = '') or (Pos('.', d) = 0) or (Pos(' ', d) > 0) then
    begin
      MsgBox('Please enter a valid domain, e.g. phone.example.com.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function JsonBool(b: Boolean): String;
begin
  if b then Result := 'true' else Result := 'false';
end;

{ Normalize the domain: strip scheme and any path, lowercase. }
function NormalizeDomain(s: String): String;
var
  p: Integer;
begin
  s := Trim(s);
  if Pos('https://', LowerCase(s)) = 1 then Delete(s, 1, 8);
  if Pos('http://', LowerCase(s)) = 1 then Delete(s, 1, 7);
  p := Pos('/', s);
  if p > 0 then s := Copy(s, 1, p - 1);
  Result := LowerCase(Trim(s));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  dir, path, json, domain: String;
begin
  if CurStep = ssPostInstall then
  begin
    domain := NormalizeDomain(DomainPage.Values[0]);
    dir := ExpandConstant('{userappdata}\CrestApps\SoftPhone');
    ForceDirectories(dir);
    path := dir + '\settings.json';
    json :=
      '{' + #13#10 +
      '  "Domain": "' + domain + '",' + #13#10 +
      '  "StartWithWindows": ' + JsonBool(PrefsPage.Values[0]) + ',' + #13#10 +
      '  "RingtoneEnabled": ' + JsonBool(PrefsPage.Values[1]) + ',' + #13#10 +
      '  "AlwaysOnTop": ' + JsonBool(PrefsPage.Values[2]) + #13#10 +
      '}';
    SaveStringToFile(path, json, False);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { Remove the per-user autostart entry the app may have registered. }
    RegDeleteValue(HKEY_CURRENT_USER,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'CrestAppsSoftPhone');
  end;
end;
