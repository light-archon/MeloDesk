#define AppVersion "0.19.2"
#define RuntimeDir "..\artifacts\runtime"

[Setup]
AppId={{CA0DBADB-F28B-4EA0-9A3D-54B68C8ADFE7}
AppName=MeloDesk
AppVersion={#AppVersion}
AppPublisher=MeloDesk
DefaultDirName={localappdata}\Programs\MeloDesk
DefaultGroupName=MeloDesk
UsePreviousGroup=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=MeloDesk-Setup-{#AppVersion}-x64
SetupIconFile={#RuntimeDir}\MeloDesk.ico
UninstallDisplayIcon={app}\MeloDesk.ico
LicenseFile={#RuntimeDir}\LICENSE
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=MeloDesk.exe,Telegram.exe,MusiGram.MusicHost.exe,MeloDesk.MusicHost.exe
RestartApplications=no
DisableProgramGroupPage=yes
UninstallDisplayName=MeloDesk
VersionInfoDescription=MeloDesk Installer
VersionInfoProductName=MeloDesk

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#RuntimeDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.log,*.pyc,tdata\*,telegram-app.local.json,promotions.json,verification.json"

Source: "{#RuntimeDir}\promotions.json"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Icons]
Name: "{userdesktop}\MeloDesk"; Filename: "{app}\MeloDesk.exe"; WorkingDir: "{app}"; IconFilename: "{app}\MeloDesk.ico"
Name: "{group}\MeloDesk"; Filename: "{app}\MeloDesk.exe"; WorkingDir: "{app}"; IconFilename: "{app}\MeloDesk.ico"
Name: "{group}\Удалить MeloDesk"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\MeloDesk.exe"; Description: "Запустить MeloDesk"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\MeloDesk.exe"; Parameters: "-quit"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; RunOnceId: "QuitMeloDesk"

[InstallDelete]
Type: files; Name: "{app}\Telegram.exe"
Type: files; Name: "{app}\MusiGram.ico"
Type: files; Name: "{app}\MusicHost\MusiGram.MusicHost.exe"
Type: files; Name: "{app}\MusicHost\MusiGram.MusicHost.dll"
Type: files; Name: "{app}\MusicHost\MusiGram.MusicHost.deps.json"
Type: files; Name: "{app}\MusicHost\MusiGram.MusicHost.runtimeconfig.json"
Type: files; Name: "{userdesktop}\MusiGram.lnk"
Type: files; Name: "{userprograms}\MusiGram\MusiGram.lnk"
Type: files; Name: "{userprograms}\MusiGram\Удалить MusiGram.lnk"
Type: dirifempty; Name: "{userprograms}\MusiGram"
