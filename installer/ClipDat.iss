#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif

[Setup]
AppId={{4D632719-C5D2-4F4A-BF8A-509C8C6FCF31}
AppName=ClipDat
AppVersion={#AppVersion}
AppPublisher=ClipDat
DefaultDirName={localappdata}\Programs\ClipDat
DefaultGroupName=ClipDat
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=ClipDat-Setup-v{#AppVersion}-x64
SetupIconFile=..\Clippr\Assets\ClipprTray.ico
UninstallDisplayIcon={app}\Assets\ClipprTray.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AppMutex=Local\Clippr.SingleInstance
DisableProgramGroupPage=yes
VersionInfoVersion={#AppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\ClipDat"; Filename: "{app}\Clippr.exe"; IconFilename: "{app}\Assets\ClipprTray.ico"
Name: "{autodesktop}\ClipDat"; Filename: "{app}\Clippr.exe"; IconFilename: "{app}\Assets\ClipprTray.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\Clippr.exe"; Description: "Launch ClipDat"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ClipDat');
end;
