; Inno Setup script for Camera Finder.
; Build: iscc /DAppVersion=0.1.0 /DSourceDir=..\publish installer\CameraFinder.iss
; The app is published self-contained, so the .NET runtime is included - nothing else to install.
; Npcap (optional, for passive listening) cannot legally be bundled, so setup offers to open its download page.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
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
LicenseFile=..\LICENSE

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

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
function NpcapInstalled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\Npcap\wpcap.dll'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if (CurStep = ssPostInstall) and (not WizardSilent()) and (not NpcapInstalled()) then
  begin
    if MsgBox('Optional: Npcap is not installed.' + #13#10 + #13#10 +
              'Camera Finder works without it, but with Npcap it can also listen to the network ' +
              'and find devices in IP ranges you do not know yet.' + #13#10 + #13#10 +
              'Npcap is free for up to 5 computers. (It is already installed if you have Wireshark.)' + #13#10 + #13#10 +
              'Open the Npcap download page now?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://npcap.com/#download', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
  end;
end;
