; NEXORA one-click installer (Inno Setup 6).
; Installs the self-contained portable build per-user — no administrator rights and no extra runtimes required.
; Build: iscc /DAppVersion=1.0.0 /DSourceDir=..\..\artifacts\portable\win-x64 build\installer\NEXORA.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\portable\win-x64"
#endif

[Setup]
AppId={{7E3C1F42-9B6A-4D8E-A2C5-5F1B3D9E8A61}
AppName=NEXORA
AppVersion={#AppVersion}
AppVerName=NEXORA {#AppVersion}
AppPublisher=NEXORA
AppComments=Optimize. Measure. Dominate.
DefaultDirName={localappdata}\Programs\NEXORA
DefaultGroupName=NEXORA
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\..\artifacts
OutputBaseFilename=NEXORA-Setup
SetupIconFile=..\..\src\Nexora.App\Assets\Nexora.ico
UninstallDisplayIcon={app}\NEXORA.exe
UninstallDisplayName=NEXORA
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\NEXORA"; Filename: "{app}\NEXORA.exe"
Name: "{group}\Uninstall NEXORA"; Filename: "{uninstallexe}"
Name: "{autodesktop}\NEXORA"; Filename: "{app}\NEXORA.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\NEXORA.exe"; Description: "Launch NEXORA"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the "start with Windows" entry if the user enabled it.
Filename: "{sys}\reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v NEXORA /f"; Flags: runhidden; RunOnceId: "RemoveRunKey"
