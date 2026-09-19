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
DefaultGroupName=Remote Debugger
DisableProgramGroupPage=yes
DefaultDirName={autopf}\RemoteDebugger
DisableDirPage=yes
UsePreviousAppDir=no
PrivilegesRequired=admin
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
Source: "{#RelayProfilePath}"; DestDir: "{app}"; DestName: "RemoteDebugger-Internet.rdrelay"; Flags: ignoreversion deleteafterinstall
#endif
Source: "..\artifacts\release\RemoteDebugger.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\release\RemoteDebugger.exe"; DestName: "RemoteDebugger-InstallerHelper.exe"; Flags: dontcopy
Source: "..\artifacts\release\RemoteDebugger.publisher.cer"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\artifacts\release\build-info.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\*.md"; DestDir: "{app}\docs"; Excludes: "VALIDATION*.md"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{commonprograms}\Remote Debugger"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,Remote Debugger}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[CustomMessages]
english.InstallerShutdownFailed=The running Remote Debugger application could not be closed before installation.
french.InstallerShutdownFailed=Remote Debugger n’a pas pu être fermé avant l’installation.
spanish.InstallerShutdownFailed=No se pudo cerrar Remote Debugger antes de la instalación.
english.LegacyUninstallFailed=The previous per-user Remote Debugger installation could not be removed.
french.LegacyUninstallFailed=L’ancienne installation utilisateur de Remote Debugger n’a pas pu être supprimée.
spanish.LegacyUninstallFailed=No se pudo eliminar la instalación de usuario anterior de Remote Debugger.
english.SupportRefreshFailed=The protected Remote Debugger support service could not be refreshed.
french.SupportRefreshFailed=Le service d’assistance protégé de Remote Debugger n’a pas pu être actualisé.
spanish.SupportRefreshFailed=No se pudo actualizar el servicio de asistencia protegido de Remote Debugger.
#ifdef RelayProfilePath
english.InternetSetupFailed=Internet setup could not be saved. Run this installer again before using internet support.
french.InternetSetupFailed=La configuration Internet n'a pas pu être enregistrée. Relancez l'installation avant d'utiliser l'assistance Internet.
spanish.InternetSetupFailed=No se pudo guardar la configuración de Internet. Ejecute de nuevo el instalador antes de usar la asistencia por Internet.
#endif

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  HelperPath: String;
  ResultCode: Integer;
begin
  Result := '';
  if CompareText(RemoveBackslashUnlessRoot(WizardDirValue), ExpandConstant('{autopf}\RemoteDebugger')) <> 0 then
  begin
    Result := 'Remote Debugger requires its protected Program Files directory. Remove the /DIR override.';
    Exit;
  end;
  ExtractTemporaryFile('RemoteDebugger-InstallerHelper.exe');
  HelperPath := ExpandConstant('{tmp}\RemoteDebugger-InstallerHelper.exe');
  try
    if not ExecAsOriginalUser(HelperPath, '--installer-user-cleanup', ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      Result := CustomMessage('LegacyUninstallFailed');
      Exit;
    end;
    if ResultCode <> 0 then
    begin
      Result := CustomMessage('LegacyUninstallFailed');
      Exit;
    end;
    if not Exec(HelperPath, '--installer-shutdown', ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      Result := CustomMessage('InstallerShutdownFailed');
      Exit;
    end;
    if ResultCode <> 0 then
    begin
      Result := CustomMessage('InstallerShutdownFailed');
      Exit;
    end;
  finally
    DeleteFile(HelperPath);
  end;

end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ProfilePath: String;
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
#ifdef RelayProfilePath
    ProfilePath := ExpandConstant('{app}\RemoteDebugger-Internet.rdrelay');
    try
      if not ExecAsOriginalUser(ExpandConstant('{app}\{#AppExeName}'),
        'cli internet-import --file "' + ProfilePath + '"',
        ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        RaiseException(CustomMessage('InternetSetupFailed'));
      if ResultCode <> 0 then
        RaiseException(CustomMessage('InternetSetupFailed'));
      Log('Protected internet setup staged for the current Windows user.');
    finally
      DeleteFile(ProfilePath);
    end;
#endif
    if not ExecAsOriginalUser(ExpandConstant('{app}\{#AppExeName}'),
      '--installer-user-startup', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RaiseException('Unable to create the original user startup shortcut.');
    if ResultCode <> 0 then RaiseException('Unable to create the original user startup shortcut.');
    if not Exec(ExpandConstant('{app}\{#AppExeName}'),
      '--support-refresh', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RaiseException(CustomMessage('SupportRefreshFailed'));
    if ResultCode <> 0 then
      RaiseException(CustomMessage('SupportRefreshFailed'));
    Log('Program Files installation and protected support service refresh completed.');
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{app}\{#AppExeName}'), '--support-uninstall', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := Result and (ResultCode = 0);
  if not Result then MsgBox('Protected support could not be removed. Finish any active update and retry uninstall.', mbError, MB_OK);
end;
