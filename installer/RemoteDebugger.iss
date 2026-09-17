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
#ifdef RelayProfilePath
OutputBaseFilename=RemoteDebugger-{#AppVersion}-Private-Setup
#else
OutputBaseFilename=RemoteDebugger-{#AppVersion}-Setup
#endif
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
#ifdef RelayProfilePath
; Only passphrase-encrypted credentials may be embedded. Import stages ciphertext
; for the first-launch unlock; no passphrase is passed to the installer or CLI.
Source: "{#RelayProfilePath}"; DestName: "RemoteDebugger-Internet.rdrelay"; Flags: dontcopy
#endif
Source: "..\artifacts\release\RemoteDebugger.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\release\RemoteDebugger.publisher.cer"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\*.md"; DestDir: "{app}\docs"; Excludes: "VALIDATION*.md"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Remote Debugger"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{userstartup}\Remote Debugger"; Filename: "{app}\{#AppExeName}"; Parameters: "--startup"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,Remote Debugger}"; Flags: nowait postinstall skipifsilent

#ifdef RelayProfilePath
[CustomMessages]
english.InternetSetupFailed=Internet setup could not be saved. Run this installer again before using internet support.
french.InternetSetupFailed=La configuration Internet n'a pas pu être enregistrée. Relancez l'installation avant d'utiliser l'assistance Internet.
spanish.InternetSetupFailed=No se pudo guardar la configuración de Internet. Ejecute de nuevo el instalador antes de usar la asistencia por Internet.

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ProfilePath: String;
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    ExtractTemporaryFile('RemoteDebugger-Internet.rdrelay');
    ProfilePath := ExpandConstant('{tmp}\RemoteDebugger-Internet.rdrelay');
    try
      if not Exec(ExpandConstant('{app}\{#AppExeName}'),
        'cli internet-import --file "' + ProfilePath + '"',
        ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        RaiseException(CustomMessage('InternetSetupFailed'));
      if ResultCode <> 0 then
        RaiseException(CustomMessage('InternetSetupFailed'));
      Log('Protected internet setup staged for the current Windows user.');
    finally
      DeleteFile(ProfilePath);
    end;
  end;
end;
#endif
