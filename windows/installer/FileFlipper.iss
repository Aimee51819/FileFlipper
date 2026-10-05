; Inno Setup script for FileFlipper for Windows.
; Build:  iscc /DAppVersion=1.6.0 /DSourceExe=..\..\build\windows\FileFlipper.exe FileFlipper.iss
; Installs per user (no administrator prompt) into %LOCALAPPDATA%\Programs\FileFlipper.

#ifndef AppVersion
  #define AppVersion "1.6.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\..\build\windows\FileFlipper.exe"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\build\windows"
#endif

[Setup]
AppId={{5B3D3C0E-7F1A-4C55-9C2B-6E8F0F1A2D41}
AppName=FileFlipper
AppVersion={#AppVersion}
AppVerName=FileFlipper {#AppVersion}
AppPublisher=Aimee Sun
AppPublisherURL=https://github.com/Aimee51819/FileFlipper
AppSupportURL=https://github.com/Aimee51819/FileFlipper/issues
AppUpdatesURL=https://fileflipper.app/
DefaultDirName={localappdata}\Programs\FileFlipper
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=FileFlipper-Windows-Setup
SetupIconFile=..\FileFlipper\Assets\FileFlipper.ico
UninstallDisplayIcon={app}\FileFlipper.exe
UninstallDisplayName=FileFlipper
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
LicenseFile=..\..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif

[CustomMessages]
english.StartupTask=Start FileFlipper when I sign in to Windows (recommended)
english.LaunchApp=Open FileFlipper now
#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
chinesesimplified.StartupTask=开机时自动启动 FileFlipper（推荐）
chinesesimplified.LaunchApp=现在打开 FileFlipper
#endif

[Tasks]
Name: "startup"; Description: "{cm:StartupTask}"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "FileFlipper.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\FileFlipper"; Filename: "{app}\FileFlipper.exe"

[Registry]
; Start with Windows (the app's own menu can turn this on and off later).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FileFlipper"; ValueData: """{app}\FileFlipper.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue
; Make sure uninstalling removes the startup entry even if it was turned on from the app later.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "FileFlipper"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\FileFlipper"; Flags: uninsdeletekey

[Run]
Filename: "{app}\FileFlipper.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM FileFlipper.exe /F"; Flags: runhidden; RunOnceId: "StopFileFlipper"

[UninstallDelete]
; The app adds itself to Explorer's "Send to" menu on first launch.
Type: files; Name: "{usersendto}\FileFlipper.lnk"
