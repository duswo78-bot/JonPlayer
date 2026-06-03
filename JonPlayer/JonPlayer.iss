[Setup]
AppName=JonPlayer
AppVersion=1.0.0
DefaultDirName={autopf}\JonPlayer
DefaultGroupName=JonPlayer
OutputBaseFilename=JonPlayer_Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
DisableProgramGroupPage=yes

[Files]
Source: "publish_release\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\JonPlayer"; Filename: "{app}\JonPlayerApp.exe"; IconFilename: "{app}\JonPlayerApp.exe"
Name: "{commondesktop}\JonPlayer"; Filename: "{app}\JonPlayerApp.exe"; IconFilename: "{app}\JonPlayerApp.exe"

[Run]
Filename: "{app}\JonPlayerApp.exe"; Description: "Launch JonPlayer"; Flags: nowait postinstall skipifsilent
