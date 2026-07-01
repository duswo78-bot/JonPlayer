#define MyAppVersion "1.0.0"
[Setup]
AppName=JonPlayer
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\JonPlayer
DefaultGroupName=JonPlayer
OutputBaseFilename=JonPlayer_Setup_v{#MyAppVersion}
OutputDir=Output
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
DisableProgramGroupPage=yes
ChangesAssociations=yes
LicenseFile=EULA.txt
SetupIconFile=Assets\logo.ico

[Types]
Name: "full"; Description: "Full installation (Includes AI Subtitles)"
Name: "compact"; Description: "Compact installation (No AI Subtitles)"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "main"; Description: "Main Application Files"; Types: full compact custom; Flags: fixed
Name: "whisper"; Description: "AI Subtitle Generation Module (Whisper) - Note: AI subtitles are not perfect"; Types: full

[Tasks]
Name: "fileassoc"; Description: "Associate media files with JonPlayer"; GroupDescription: "File associations:"
Name: "fileassoc\video"; Description: "Video files"; GroupDescription: "File associations:"
Name: "fileassoc\video\mp4"; Description: ".mp4"; GroupDescription: "File associations:"
Name: "fileassoc\video\mkv"; Description: ".mkv"; GroupDescription: "File associations:"
Name: "fileassoc\video\avi"; Description: ".avi"; GroupDescription: "File associations:"
Name: "fileassoc\video\mov"; Description: ".mov"; GroupDescription: "File associations:"
Name: "fileassoc\video\wmv"; Description: ".wmv"; GroupDescription: "File associations:"
Name: "fileassoc\video\flv"; Description: ".flv"; GroupDescription: "File associations:"
Name: "fileassoc\video\webm"; Description: ".webm"; GroupDescription: "File associations:"
Name: "fileassoc\video\ts"; Description: ".ts"; GroupDescription: "File associations:"
Name: "fileassoc\video\m2ts"; Description: ".m2ts"; GroupDescription: "File associations:"
Name: "fileassoc\audio"; Description: "Audio files"; GroupDescription: "File associations:"
Name: "fileassoc\audio\mp3"; Description: ".mp3"; GroupDescription: "File associations:"
Name: "fileassoc\audio\flac"; Description: ".flac"; GroupDescription: "File associations:"
Name: "fileassoc\audio\wav"; Description: ".wav"; GroupDescription: "File associations:"
Name: "fileassoc\audio\aac"; Description: ".aac"; GroupDescription: "File associations:"
Name: "fileassoc\audio\ogg"; Description: ".ogg"; GroupDescription: "File associations:"
Name: "fileassoc\audio\m4a"; Description: ".m4a"; GroupDescription: "File associations:"

[Files]
Source: "bin\Release\net8.0-windows\*"; DestDir: "{app}"; Excludes: "*whisper*.dll,Whisper.net.dll,ggml-small.bin,win-x64\*,publish\*,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: main

; Whisper AI Subtitle Files
Source: "bin\Release\net8.0-windows\Whisper.net.dll"; DestDir: "{app}"; Flags: ignoreversion; Components: whisper
Source: "bin\Release\net8.0-windows\runtimes\*whisper*.dll"; DestDir: "{app}\runtimes"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: whisper
Source: "bin\Debug\net8.0-windows\ggml-small.bin"; DestDir: "{app}"; Flags: ignoreversion; Components: whisper

[Icons]
Name: "{group}\JonPlayer"; Filename: "{app}\JonPlayerApp.exe"; IconFilename: "{app}\JonPlayerApp.exe"
Name: "{commondesktop}\JonPlayer"; Filename: "{app}\JonPlayerApp.exe"; IconFilename: "{app}\JonPlayerApp.exe"

[Registry]
; Whisper installation flag
Root: HKCU; Subkey: "Software\JonPlayer"; ValueType: dword; ValueName: "WhisperInstalled"; ValueData: "1"; Components: whisper; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\JonPlayer"; ValueType: dword; ValueName: "WhisperInstalled"; ValueData: "0"; Components: not whisper; Flags: uninsdeletevalue

; Custom URL Protocol for jonplayer://
Root: HKCR; Subkey: "jonplayer"; ValueType: string; ValueName: ""; ValueData: "URL:JonPlayer Protocol"; Flags: uninsdeletekey
Root: HKCR; Subkey: "jonplayer"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletekey
Root: HKCR; Subkey: "jonplayer\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\JonPlayerApp.exe,0"
Root: HKCR; Subkey: "jonplayer\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\JonPlayerApp.exe"" ""%1"""

; Custom URL Protocol for jonplayer-pip://
Root: HKCR; Subkey: "jonplayer-pip"; ValueType: string; ValueName: ""; ValueData: "URL:JonPlayer PIP Protocol"; Flags: uninsdeletekey
Root: HKCR; Subkey: "jonplayer-pip"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletekey
Root: HKCR; Subkey: "jonplayer-pip\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\JonPlayerApp.exe,0"
Root: HKCR; Subkey: "jonplayer-pip\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\JonPlayerApp.exe"" ""%1"""

; File associations
Root: HKCR; Subkey: "JonPlayer.Media"; ValueType: string; ValueName: ""; ValueData: "JonPlayer Media File"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKCR; Subkey: "JonPlayer.Media\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\JonPlayerApp.exe,0"; Tasks: fileassoc
Root: HKCR; Subkey: "JonPlayer.Media\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\JonPlayerApp.exe"" ""%1"""; Tasks: fileassoc

Root: HKCR; Subkey: ".mp4\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\video\mp4
Root: HKCR; Subkey: ".mkv\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\video\mkv
Root: HKCR; Subkey: ".avi\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\video\avi
Root: HKCR; Subkey: ".mov\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\video\mov
Root: HKCR; Subkey: ".wmv\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\video\wmv
Root: HKCR; Subkey: ".flv\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\video\flv
Root: HKCR; Subkey: ".webm\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\video\webm
Root: HKCR; Subkey: ".ts\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\video\ts
Root: HKCR; Subkey: ".m2ts\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\video\m2ts

Root: HKCR; Subkey: ".mp3\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\audio\mp3
Root: HKCR; Subkey: ".flac\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\audio\flac
Root: HKCR; Subkey: ".wav\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\audio\wav
Root: HKCR; Subkey: ".aac\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\audio\aac
Root: HKCR; Subkey: ".ogg\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\audio\ogg
Root: HKCR; Subkey: ".m4a\OpenWithProgids"; ValueType: string; ValueName: "JonPlayer.Media"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc\audio\m4a

[Run]
Filename: "{app}\JonPlayerApp.exe"; Description: "Launch JonPlayer"; Flags: nowait postinstall skipifsilent
