#ifndef AppVersion
  #error Compila mediante tools/Build-Installer.ps1
#endif
#ifndef DataRoot
  #define DataRoot ""
#endif
#ifdef Experimental
 #ifdef InstallerUiAudit
  #define ProductName "Destiny Launcher (UI test)"
  #define ProductFolder "Destiny Launcher UI test"
  #define ProductId "{6DB7D6A9-BFDC-4709-852B-2C90B7934AA4}"
  #define RegistryKey "Software\DestinyDynasty\Launcher.UiTest"
 #else
  #define ProductName "Destiny Launcher (prueba)"
  #define ProductFolder "Destiny Launcher Experiment"
  #define ProductId "{0A1B7DEE-86D3-4A65-9709-0CB9ECC141F2}"
  #define RegistryKey "Software\DestinyDynasty\Launcher.Experiment"
 #endif
#else
  #define ProductName "Destiny Launcher"
  #define ProductFolder "Destiny Launcher"
  #define ProductId "{D7E98A51-6E09-4F63-929B-34DDF0B24B3C}"
  #define RegistryKey "Software\DestinyDynasty\Launcher"
#endif

[Setup]
AppId={{#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} {#AppVersion}
AppPublisher=Destiny Dynasty
AppPublisherURL=https://github.com/Destiny-Dynasty/Destiny-Launcher
AppSupportURL=https://github.com/Destiny-Dynasty/Destiny-Launcher/issues
AppUpdatesURL=https://github.com/Destiny-Dynasty/Destiny-Launcher/releases
VersionInfoProductName=Destiny Launcher
VersionInfoCompany=Destiny Dynasty
#ifdef InstallerUiAudit
DefaultDirName={#UiAuditRoot}\program
#else
DefaultDirName={localappdata}\Programs\{#ProductFolder}
#endif
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#ProductName}
UninstallDisplayIcon={app}\DestinyLauncher.exe
SetupIconFile=..\DestinyLauncher\Assets\destiny-launcher.ico
OutputDir={#OutputPath}
#ifdef Offline
OutputBaseFilename=DestinyLauncherSetup
#else
OutputBaseFilename=DestinyLauncherWebSetup
#endif
Compression=lzma2/normal
SolidCompression=yes
ArchiveExtraction=full
WizardStyle=modern
WizardSizePercent=100,100
WizardResizable=no
DisableWelcomePage=no
DisableDirPage=yes
DisableReadyPage=yes
DisableFinishedPage=no
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
#ifndef Experimental
AppMutex=Local\DestinyDynasty.Launcher
#endif
#ifdef UseSignedUninstaller
SignedUninstaller=yes
SignedUninstallerDir={#SignedUninstallerCache}
#endif
#ifdef UseSignTool
SignTool=destiny
#endif
[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; Flags: unchecked
[Files]
Source: "{#BackgroundImage}"; DestName: "setup-background.png"; Flags: dontcopy
Source: "{#LogoImage}"; DestName: "setup-logo.png"; Flags: dontcopy
Source: "{#ArtworkDir}\setup-close.png"; Flags: dontcopy
Source: "{#ArtworkDir}\setup-minimize.png"; Flags: dontcopy
#ifdef Offline
Source: "{#PayloadDir}\DestinyLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
#else
Source: "{#PayloadUrl}"; DestDir: "{app}"; DestName: "DestinyLauncher-win-x64.zip"; ExternalSize: {#PayloadSize}; Hash: "{#PayloadHash}"; Flags: external download extractarchive ignoreversion recursesubdirs createallsubdirs
#endif
[Icons]
Name: "{autoprograms}\{#ProductName}"; Filename: "{app}\DestinyLauncher.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\DestinyLauncher.exe"; WorkingDir: "{app}"; Tasks: desktopicon
[Registry]
Root: HKCU; Subkey: "{#RegistryKey}"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "{#RegistryKey}"; ValueType: string; ValueName: "DataRoot"; ValueData: "{code:GetDataRoot}"
[Run]
Filename: "{app}\DestinyLauncher.exe"; Flags: nowait; Check: AutoRestart
[UninstallDelete]
Type: files; Name: "{app}\launcher-installation.json"

[Code]
var
  UpdateHandoffReady: Boolean;
  InstallationFinished: Boolean;
function OpenProcess(Access: LongWord; InheritHandle: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetCurrentProcessId: LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';
#include "DestinyInstallerUI.iss"
function AutoRestart: Boolean;
begin
  Result := ExpandConstant('{param:AUTORESTART|0}') = '1';
end;
function GetDataRoot(Param: String): String;
begin
#ifdef Experimental
  Result := '{#DataRoot}';
#else
  Result := ExpandConstant('{localappdata}\DestinyLauncher');
#endif
end;
function JsonPath(Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;
function InitializeSetup: Boolean;
var
  Parent: THandle;
  Pid: Integer;
begin
  Result := True;
#ifdef InstallerUiAudit
  // A UI test must never install into an existing launcher or a player's path.
  if (ExpandConstant('{param:DIR|}') <> '') and
     (CompareText(ExpandConstant('{param:DIR|}'), '{#UiAuditRoot}\program') <> 0) then begin
    Result := False;
    exit;
  end;
#endif
  Pid := StrToIntDef(ExpandConstant('{param:UPDATEPID|0}'), 0);
  if Pid > 0 then begin
    Parent := OpenProcess($00100000, False, Pid);
    if Parent <> 0 then begin
      Result := WaitForSingleObject(Parent, 30000) = 0;
      CloseHandle(Parent);
      if not Result then MsgBox('Cierra Destiny Launcher antes de actualizar.', mbError, MB_OK);
    end;
  end;
  UpdateHandoffReady := Result and (ExpandConstant('{param:UPDATE|0}') = '1');
end;
procedure CurStepChanged(Step: TSetupStep);
var
  Metadata: String;
begin
  if Step = ssPostInstall then begin
    Metadata := '{"DataRoot":"' + JsonPath(GetDataRoot('')) + '","Experimental":';
#ifdef Experimental
    Metadata := Metadata + 'true}';
#else
    Metadata := Metadata + 'false}';
#endif
    if not SaveStringToFile(ExpandConstant('{app}\launcher-installation.json'), UTF8Encode(Metadata), False) then
      RaiseException('No se pudo registrar la instalación.');
  end;
  if Step = ssDone then InstallationFinished := True;
end;
procedure DeinitializeSetup;
var
  Code: Integer;
begin
  UiDestroySurface;
  // An unsuccessful upgrade must give the player the previous launcher back.
  // Successful installs restart through [Run], so this path never duplicates it.
  if UpdateHandoffReady and not InstallationFinished and FileExists(ExpandConstant('{app}\DestinyLauncher.exe')) then
    Exec(ExpandConstant('{app}\DestinyLauncher.exe'), '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, Code);
end;
function InitializeUninstall: Boolean;
var
  Code: Integer;
  Parent: THandle;
  Pid: Integer;
begin
  Result := True;
  if not UninstallSilent then begin
    // Direct launches and Windows' Uninstall entry both come here. Restart the
    // native engine without its confirmation, progress form or completion box;
    // DESTINYUI explicitly retains our own confirmation in the silent child.
    Result := False;
    if not Exec(ExpandConstant('{uninstallexe}'),
      '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG /DESTINYUI=1 /DESTINYPARENT=' + IntToStr(GetCurrentProcessId),
      ExpandConstant('{app}'), SW_HIDE, ewNoWait, Code) then
      MsgBox('No se pudo iniciar el desinstalador.', mbError, MB_OK);
    exit;
  end;
  if ExpandConstant('{param:DESTINYUI|0}') <> '1' then exit;
  Pid := StrToIntDef(ExpandConstant('{param:DESTINYPARENT|0}'), 0);
  if Pid > 0 then begin
    Parent := OpenProcess($00100000, False, Pid);
    if Parent <> 0 then begin
      Result := WaitForSingleObject(Parent, 30000) = 0;
      CloseHandle(Parent);
    end;
  end;
end;
procedure CurUninstallStepChanged(Step: TUninstallStep);
var
  Code: Integer;
  Root: String;
  Confirmed: Boolean;
begin
  // Only the interactive handoff shows Destiny's dialog. Administrative silent
  // uninstall keeps all personal data and never opens UI. Abort still stops the
  // engine before any program files are removed when our dialog is cancelled.
  if (Step <> usUninstall) or (ExpandConstant('{param:DESTINYUI|0}') <> '1') then exit;
  Root := GetDataRoot('');
  RegQueryStringValue(HKCU, '{#RegistryKey}', 'DataRoot', Root);
  if FileExists(ExpandConstant('{app}\DestinyLauncher.exe')) then begin
    Confirmed := Exec(ExpandConstant('{app}\DestinyLauncher.exe'), '--uninstall --data-root "' + Root + '"',
      ExpandConstant('{app}'), SW_SHOWNORMAL, ewWaitUntilTerminated, Code);
    if not Confirmed or (Code <> 0) then Abort;
  end else
    MsgBox('No se encontró el launcher. Se conservarán los datos del juego.', mbInformation, MB_OK);
end;
