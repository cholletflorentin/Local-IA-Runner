#define ProjectRoot GetEnv("LOCALIA_PROJECT_ROOT")
#define StageDir GetEnv("LOCALIA_INSTALLER_STAGE")
#define OutputDir GetEnv("LOCALIA_INSTALLER_OUTPUT")
#define AppVersion GetEnv("LOCALIA_INSTALLER_VERSION")

[Setup]
AppId={{A5BAEC79-7A87-4978-A8E6-99F94FC40F95}
AppName=Local IA Runner
AppVersion={#AppVersion}
AppVerName=Local IA Runner {#AppVersion}
AppPublisher=Local IA Runner
DefaultDirName={localappdata}\Programs\Local IA Runner
DefaultGroupName=Local IA Runner
UninstallDisplayName=Local IA Runner
UninstallDisplayIcon={app}\Local IA Runner.exe
SetupIconFile={#ProjectRoot}\WinFormsApp\Assets\LocalIARunner.ico
OutputDir={#OutputDir}
OutputBaseFilename=LocalIARunner-Setup-x64
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=Installateur de Local IA Runner
VersionInfoProductName=Local IA Runner
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=lowest
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
UninstallFilesDir={app}\Uninstall

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\Local IA Runner\Local IA Runner"; Filename: "{app}\Local IA Runner.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Local IA Runner.exe"
Name: "{autodesktop}\Local IA Runner"; Filename: "{app}\Local IA Runner.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Local IA Runner.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Local IA Runner.exe"; Description: "Lancer Local IA Runner"; Flags: nowait postinstall skipifsilent
