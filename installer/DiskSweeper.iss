; DiskSweeper installer: per-machine (or per-user) copy, Start menu shortcut, uninstaller.
; The app exe stays asInvoker. This script's admin prompt is only for copying files
; into Program Files. Build it with build/Publish-Release.ps1 (Inno Setup 6.3+ or 7).

#ifndef MyAppVersion
  #define MyAppVersion GetEnv("DISKSWEEPER_VERSION")
#endif
#ifndef MyAppVersionInfo
  #define MyAppVersionInfo GetEnv("DISKSWEEPER_FILE_VERSION")
#endif
#if MyAppVersion == ""
  #error Set DISKSWEEPER_VERSION before compiling (build/Publish-Release.ps1 does this).
#endif
#if MyAppVersionInfo == ""
  #error Set DISKSWEEPER_FILE_VERSION before compiling (build/Publish-Release.ps1 does this).
#endif

#define MyAppName "DiskSweeper"
#define MyAppExeName "DiskSweeper.exe"

[Setup]
; Stable id so an upgrade replaces the previous install.
AppId={{5E09D60C-12BD-452F-9B7C-620B71098B54}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=process631
AppPublisherURL=https://github.com/process631/DiskSweeper
AppSupportURL=https://github.com/process631/DiskSweeper/issues
AppUpdatesURL=https://github.com/process631/DiskSweeper/releases/latest
VersionInfoVersion={#MyAppVersionInfo}
VersionInfoCompany=process631
VersionInfoProductName=DiskSweeper
VersionInfoDescription=DiskSweeper Setup
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\artifacts\dist
OutputBaseFilename=DiskSweeper-Setup
SetupIconFile=..\src\DiskSweeper.App\Assets\DiskSweeper.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\win-x64\DiskSweeper.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\publish\win-x64\dsweep.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "Portable.txt"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "See what's taking up space on your PC"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
