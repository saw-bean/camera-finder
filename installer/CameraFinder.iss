; Inno Setup script for Camera Finder.
; Build: iscc /DAppVersion=0.1.0 /DSourceDir=..\publish installer\CameraFinder.iss
; The app is published self-contained, so the .NET runtime is included - nothing else to install.
; Npcap (optional, for passive listening) cannot legally be bundled (free licence), so setup can download the
; official installer from npcap.com and start it; the user accepts Npcap's own licence in its wizard.
; To move to a newer Npcap: update NpcapVersion and NpcapSha256 (sha256 of https://npcap.com/dist/npcap-<ver>.exe).

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define NpcapVersion "1.90"
#define NpcapSha256 "682b57e1c05ef375931975c9d6ded40888f4bfdf9f4b11571e654f962f1199b6"

#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6E0B6F3A-2C47-4C1B-9B8E-3F1F2D6A9C11}
AppName=Camera Finder
AppVersion={#AppVersion}
AppPublisher=Sabin Khatri
DefaultDirName={autopf}\Camera Finder
DefaultGroupName=Camera Finder
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=CameraFinder-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\CameraFinder.exe
SetupIconFile=..\src\CameraFinder.App\app.ico
LicenseFile=..\LICENSE

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "npcap"; Description: "Download and install Npcap {#NpcapVersion} (recommended - finds devices in unknown IP ranges; free for up to 5 computers)"; GroupDescription: "Optional components:"; Check: not NpcapInstalled

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Camera Finder"; Filename: "{app}\CameraFinder.exe"
Name: "{group}\Uninstall Camera Finder"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Camera Finder"; Filename: "{app}\CameraFinder.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CameraFinder.exe"; Description: "Start Camera Finder"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\CameraFinder"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  NpcapDownloaded: Boolean;
  NpcapFailed: Boolean;

function NpcapInstalled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\Npcap\wpcap.dll'));
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage('Downloading Npcap', 'Downloading the official Npcap installer from npcap.com...', @OnDownloadProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsTaskSelected('npcap') and (not NpcapInstalled()) then
  begin
    DownloadPage.Clear;
    DownloadPage.Add('https://npcap.com/dist/npcap-{#NpcapVersion}.exe', 'npcap-setup.exe', '{#NpcapSha256}');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        NpcapDownloaded := True;
      except
        NpcapFailed := True;
        Log('Npcap download failed: ' + GetExceptionMessage);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

procedure OpenNpcapPage();
var
  ErrorCode: Integer;
begin
  ShellExec('open', 'https://npcap.com/#download', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep <> ssPostInstall) or WizardSilent() then Exit;

  if NpcapDownloaded then
  begin
    WizardForm.StatusLabel.Caption := 'Waiting for the Npcap installer to finish...';
    if not Exec(ExpandConstant('{tmp}\npcap-setup.exe'), '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      NpcapFailed := True;
  end;

  if NpcapFailed and (not NpcapInstalled()) then
  begin
    if MsgBox('Npcap could not be downloaded or installed (no internet?).' + #13#10 + #13#10 +
              'Camera Finder works without it, but with Npcap it can also listen to the network ' +
              'and find devices in IP ranges you do not know yet.' + #13#10 + #13#10 +
              'Open the Npcap download page now?', mbConfirmation, MB_YESNO) = IDYES then
      OpenNpcapPage();
  end;
end;
