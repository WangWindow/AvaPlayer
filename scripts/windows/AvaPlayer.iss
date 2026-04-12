#define MyAppName "AvaPlayer"

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef MyPublishDir
  #error MyPublishDir is not defined.
#endif

#ifndef MyOutputDir
  #error MyOutputDir is not defined.
#endif

#ifndef MyRepoRoot
  #error MyRepoRoot is not defined.
#endif

[Setup]
AppId={{5A85D7E4-E6E0-493D-B51E-B2AFD5527614}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=AvaPlayer
DefaultDirName={localappdata}\Programs\AvaPlayer
DefaultGroupName=AvaPlayer
DisableProgramGroupPage=yes
OutputDir={#MyOutputDir}
OutputBaseFilename=AvaPlayer-{#MyAppVersion}-win-x64-setup
SetupIconFile={#MyRepoRoot}\Resources\logo.ico
UninstallDisplayIcon={app}\AvaPlayer.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\AvaPlayer"; Filename: "{app}\AvaPlayer.exe"; IconFilename: "{#MyRepoRoot}\Resources\logo.ico"
Name: "{autodesktop}\AvaPlayer"; Filename: "{app}\AvaPlayer.exe"; Tasks: desktopicon; IconFilename: "{#MyRepoRoot}\Resources\logo.ico"

[Run]
Filename: "{app}\AvaPlayer.exe"; Description: "Run AvaPlayer"; Flags: nowait postinstall skipifsilent
