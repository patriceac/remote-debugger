#ifndef AppVersion
  #error AppVersion must be supplied by scripts\Build-Installer.ps1
#endif

#define AppName "Remote Debugger"
#define AppPublisher "Remote Debugger"
#define AppExeName "RemoteDebugger.exe"

[Setup]
AppId={{B12489BE-DF12-4DD2-AFD4-FB82B032BE05}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\Remote Debugger
DefaultGroupName=Remote Debugger
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=RemoteDebugger-{#AppVersion}-Setup
SetupIconFile=..\src\RemoteDebugger\Assets\RemoteDebugger.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} installer
VersionInfoCompany={#AppPublisher}
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=no
UsePreviousLanguage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "..\artifacts\release\RemoteDebugger.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\release\RemoteDebugger.publisher.cer"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\*.md"; DestDir: "{app}\docs"; Excludes: "VALIDATION*.md"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Remote Debugger"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,Remote Debugger}"; Flags: nowait postinstall skipifsilent
