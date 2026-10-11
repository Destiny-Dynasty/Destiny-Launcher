#ifndef PayloadSize
  #define PayloadSize 0
#endif

[Setup]
AppId={{D7E98A51-6E09-4F63-929B-34DDF0B24B3C}
AppName=Destiny Launcher
AppVersion=0.1.0
AppPublisher=Destiny Dynasty
AppPublisherURL=https://github.com/Destiny-Dynasty/Destiny-Launcher
AppSupportURL=https://github.com/Destiny-Dynasty/Destiny-Launcher/issues
AppUpdatesURL=https://github.com/Destiny-Dynasty/Destiny-Launcher/releases
DefaultDirName={localappdata}\Programs\Destiny Launcher
DefaultGroupName=Destiny Launcher
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayName=Destiny Launcher
UninstallDisplayIcon={app}\DestinyLauncher.exe
SetupIconFile=..\DestinyLauncher\Assets\destiny-launcher.ico
OutputDir=..\publish
OutputBaseFilename=DestinyLauncherSetup
Compression=lzma2/max
WizardStyle=modern
SetupLogging=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"; Flags: unchecked

[Files]
Source: "https://github.com/Destiny-Dynasty/Destiny-Launcher/releases/latest/download/DestinyLauncher-win-x64.zip"; DestDir: "{app}"; DestName: "DestinyLauncher-win-x64.zip"; ExternalSize: {#PayloadSize}; Flags: external download extractarchive ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Destiny Launcher"; Filename: "{app}\DestinyLauncher.exe"; WorkingDir: "{app}"; IconFilename: "{app}\DestinyLauncher.exe"
Name: "{autodesktop}\Destiny Launcher"; Filename: "{app}\DestinyLauncher.exe"; WorkingDir: "{app}"; IconFilename: "{app}\DestinyLauncher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DestinyLauncher.exe"; Description: "Iniciar Destiny Launcher"; Flags: nowait postinstall skipifsilent
