; Build with .\build-installer.ps1. The script publishes Origin.App first.
#define PublishDir "..\artifacts\publish\Origin"
#define AppExeName "OriginHTPC.exe"

; Compiled without the script, take the version from the published executable.
#ifndef Version
#define Version GetVersionNumbersString(AddBackslash(SourcePath) + PublishDir + "\" + AppExeName)
#endif

[Setup]
AppId={{DEFF7CDB-2F02-4336-B730-88C72582CED6}
AppName=Origin
AppVersion={#Version}
AppPublisher=Origin
AppCopyright=Copyright (C) 2026 Jack Wiggins
AppPublisherURL=https://github.com/jack-wiggins/OriginHTPC
AppSupportURL=https://github.com/jack-wiggins/OriginHTPC/issues
; Not "Origin": EA's game launcher already installs to Program Files (x86)\Origin.
DefaultDirName={autopf}\OriginHTPC
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=OriginHTPC-Setup-{#Version}
Compression=lzma2/ultra64
SolidCompression=yes
; OriginHTPC.exe is AnyCPU, so it runs as a 64-bit process on 64-bit Windows.
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 7 SP1, the oldest Windows that runs .NET Framework 4.8.
MinVersion=6.1sp1
; Origin runs as a standard user and keeps its data in %LOCALAPPDATA%\OriginHTPC
; (left in place on uninstall), so it can also be installed for the current user only.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName=Origin
; Matches the single-instance mutex in App.xaml.cs.
AppMutex=Local\Origin.HTPC.SingleInstance
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; LibVLC loads every DLL in its plugins folder, so plugins from an older LibVLC must not linger.
Type: filesandordirs; Name: "{app}\libvlc"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.lib"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\COPYING"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\COPYING.LESSER"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Origin"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\Origin"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,Origin}"; Flags: nowait postinstall skipifsilent

[Code]
const
  NetFramework48Release = 528040;
  NetFramework48Url = 'https://dotnet.microsoft.com/download/dotnet-framework/net48';

function HasNetFramework48: Boolean;
var
  Release: Cardinal;
begin
  if IsWin64 then
    Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release)
  else
    Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release);
  Result := Result and (Release >= NetFramework48Release);
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := HasNetFramework48;
  if not Result then
    if SuppressibleMsgBox('Origin requires Microsoft .NET Framework 4.8. Install it, then run Setup again.' + #13#10#13#10 +
      'Open the .NET Framework 4.8 download page now?', mbError, MB_YESNO, IDNO) = IDYES then
      ShellExecAsOriginalUser('open', NetFramework48Url, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;
