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
UsePreviousTasks=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
#ifdef AdminCredentialPath
Source: "{#AdminCredentialPath}"; DestDir: "{app}"; DestName: "RemoteDebugger-Admin.rdadmin"; Flags: ignoreversion deleteafterinstall
#endif
#ifdef RelayProfilePath
; Only passphrase-encrypted credentials may be embedded. Import stages ciphertext
; for the first-launch unlock; no passphrase is passed to the installer or CLI.
Source: "{#RelayProfilePath}"; DestDir: "{app}"; DestName: "RemoteDebugger-Internet.rdrelay"; Flags: ignoreversion deleteafterinstall
#endif
Source: "..\artifacts\release\RemoteDebugger.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\release\RemoteDebugger.exe"; DestName: "RemoteDebugger-InstallerHelper.exe"; Flags: dontcopy noencryption
Source: "..\artifacts\release\RemoteDebugger.publisher.cer"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\artifacts\release\build-info.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\*.md"; DestDir: "{app}\docs"; Excludes: "VALIDATION*.md"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{commonprograms}\Remote Debugger"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#AppExeName}"; Parameters: "{code:LaunchArguments}"; Description: "{cm:LaunchProgram,Remote Debugger}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Tasks]
#ifdef AdminCredentialPath
Name: "adminpc"; Description: "{cm:AdminPcOption}"; Flags: unchecked
#endif

[CustomMessages]
english.AdminPcOption=Set up this PC as an admin (requires admin password)
french.AdminPcOption=Configurer ce PC comme administrateur (mot de passe administrateur requis)
spanish.AdminPcOption=Configurar este PC como administrador (requiere contraseña de administrador)
english.AdminSetupFailed=The protected admin credential could not be staged.
french.AdminSetupFailed=Les identifiants administrateur protégés n’ont pas pu être préparés.
spanish.AdminSetupFailed=No se pudo preparar la credencial de administrador protegida.
english.AdminStatusFailed=The current admin-PC status could not be read or updated.
french.AdminStatusFailed=L’état actuel de PC administrateur n’a pas pu être lu ou mis à jour.
spanish.AdminStatusFailed=No se pudo leer o actualizar el estado actual de PC administrador.
english.InstallerShutdownFailed=The running Remote Debugger application could not be closed before installation.
french.InstallerShutdownFailed=Remote Debugger n’a pas pu être fermé avant l’installation.
spanish.InstallerShutdownFailed=No se pudo cerrar Remote Debugger antes de la instalación.
english.LegacyUninstallFailed=The previous per-user Remote Debugger installation could not be removed.
french.LegacyUninstallFailed=L’ancienne installation utilisateur de Remote Debugger n’a pas pu être supprimée.
spanish.LegacyUninstallFailed=No se pudo eliminar la instalación de usuario anterior de Remote Debugger.
english.SupportSetupFailed=The protected Remote Debugger support service could not be installed or refreshed.
french.SupportSetupFailed=Le service d’assistance protégé de Remote Debugger n’a pas pu être installé ou actualisé.
spanish.SupportSetupFailed=No se pudo instalar o actualizar el servicio de asistencia protegido de Remote Debugger.
#ifdef RelayProfilePath
english.InternetSetupFailed=Internet setup could not be saved. Run this installer again before using internet support.
french.InternetSetupFailed=La configuration Internet n'a pas pu être enregistrée. Relancez l'installation avant d'utiliser l'assistance Internet.
spanish.InternetSetupFailed=No se pudo guardar la configuración de Internet. Ejecute de nuevo el instalador antes de usar la asistencia por Internet.
#endif

[Code]
var
  AdminPcInitiallySelected: Boolean;
  AdminPcTaskInitialized: Boolean;

function InstallerHelperPath(): String;
begin
  Result := ExpandConstant('{tmp}\RemoteDebugger-InstallerHelper.exe');
  if not FileExists(Result) then ExtractTemporaryFile('RemoteDebugger-InstallerHelper.exe');
end;

function InitializeSetup(): Boolean;
var
  HelperPath: String;
  ResultCode: Integer;
begin
  Result := True;
#ifdef AdminCredentialPath
  HelperPath := InstallerHelperPath();
  if not ExecAsOriginalUser(HelperPath, 'cli admin-status', ExpandConstant('{tmp}'),
    SW_HIDE, ewWaitUntilTerminated, ResultCode) or ((ResultCode <> 0) and (ResultCode <> 1)) then
  begin
    MsgBox(CustomMessage('AdminStatusFailed'), mbError, MB_OK);
    Result := False;
    Exit;
  end;
  AdminPcInitiallySelected := ResultCode = 0;
#endif
end;

procedure InitializeAdminPcTask();
begin
#ifdef AdminCredentialPath
  if not AdminPcTaskInitialized then
  begin
    if AdminPcInitiallySelected then WizardSelectTasks('adminpc')
    else WizardSelectTasks('!adminpc');
    AdminPcTaskInitialized := True;
  end;
#endif
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectTasks then InitializeAdminPcTask();
end;

function LaunchArguments(Param: String): String;
begin
  Result := '';
#ifdef AdminCredentialPath
  if WizardIsTaskSelected('adminpc') then Result := '--admin-setup';
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  HelperPath: String;
  ResultCode: Integer;
begin
  Result := '';
  InitializeAdminPcTask();
  if CompareText(RemoveBackslashUnlessRoot(WizardDirValue), ExpandConstant('{autopf}\RemoteDebugger')) <> 0 then
  begin
    Result := 'Remote Debugger requires its protected Program Files directory. Remove the /DIR override.';
    Exit;
  end;
  HelperPath := InstallerHelperPath();
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
  RequestPath: String;
  EncodedRequest: AnsiString;
  Attempt: Integer;
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
#ifdef AdminCredentialPath
    ProfilePath := ExpandConstant('{app}\RemoteDebugger-Admin.rdadmin');
    try
      if WizardIsTaskSelected('adminpc') then
      begin
        if not ExecAsOriginalUser(ExpandConstant('{app}\{#AppExeName}'),
          'cli admin-import --file "' + ProfilePath + '"', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
          RaiseException(CustomMessage('AdminSetupFailed'));
        if ResultCode <> 0 then RaiseException(CustomMessage('AdminSetupFailed'));
      end;
      if not WizardIsTaskSelected('adminpc') then
      begin
        if not ExecAsOriginalUser(ExpandConstant('{app}\{#AppExeName}'),
          'cli admin-disable', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
          RaiseException(CustomMessage('AdminStatusFailed'));
        if ResultCode <> 0 then RaiseException(CustomMessage('AdminStatusFailed'));
      end;
    finally
      DeleteFile(ProfilePath);
    end;
#endif
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
    if FileExists(ExpandConstant('{commonappdata}\RemoteDebugger\Support\platform.json')) then
    begin
      if not Exec(ExpandConstant('{app}\{#AppExeName}'),
        '--support-refresh', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        RaiseException(CustomMessage('SupportSetupFailed'));
      if ResultCode <> 0 then RaiseException(CustomMessage('SupportSetupFailed'));
    end
    else
    begin
      RequestPath := ExpandConstant('{tmp}\RemoteDebugger-ProvisionRequest.txt');
      if not ExecAsOriginalUser(ExpandConstant('{app}\{#AppExeName}'),
        '--installer-provision-request "' + RequestPath + '"', ExpandConstant('{app}'),
        SW_HIDE, ewNoWait, ResultCode) then
        RaiseException(CustomMessage('SupportSetupFailed'));
      EncodedRequest := '';
      try
        for Attempt := 1 to 600 do
        begin
          if LoadStringFromFile(RequestPath, EncodedRequest) and (Length(EncodedRequest) > 0) then Break;
          Sleep(100);
        end;
        if Length(EncodedRequest) = 0 then RaiseException(CustomMessage('SupportSetupFailed'));
        if not Exec(ExpandConstant('{app}\{#AppExeName}'),
          '--installer-ensure-support "' + String(EncodedRequest) + '"',
          ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
          RaiseException(CustomMessage('SupportSetupFailed'));
        if ResultCode <> 0 then RaiseException(CustomMessage('SupportSetupFailed'));
      finally
        DeleteFile(RequestPath);
      end;
    end;
    Log('Program Files installation and protected support service setup completed.');
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
