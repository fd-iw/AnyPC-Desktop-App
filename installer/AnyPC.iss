; Inno Setup script for AnyPC Desktop.
; Build: iscc /DAppVersion=1.0.0 /DSourceDir=..\publish installer\AnyPC.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6C1F4E4B-7A0B-4B4E-9E0B-2B3C8A1D0A51}
AppName=AnyPC
AppVersion={#AppVersion}
AppPublisher=AnyPC
DefaultDirName={autopf}\AnyPC
DefaultGroupName=AnyPC
DisableProgramGroupPage=yes
OutputDir=..\out
OutputBaseFilename=AnyPC-Setup
SetupIconFile=..\src\AnyPC.Windows\Assets\anypc.ico
UninstallDisplayIcon={app}\AnyPC.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
WizardStyle=modern
CloseApplications=force

[Tasks]
Name: "autostart"; Description: "Start AnyPC automatically when I sign in"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\AnyPC.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\AnyPC"; Filename: "{app}\AnyPC.exe"
Name: "{autodesktop}\AnyPC"; Filename: "{app}\AnyPC.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AnyPC"; ValueData: """{app}\AnyPC.exe"" --minimized"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
; Allow the iPhone to reach AnyPC on Private (home) networks.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""AnyPC"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""AnyPC"" dir=in action=allow program=""{app}\AnyPC.exe"" enable=yes profile=private,domain"; Flags: runhidden waituntilterminated; StatusMsg: "Adding Windows Firewall rule..."
Filename: "{app}\AnyPC.exe"; Description: "Launch AnyPC"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im AnyPC.exe"; Flags: runhidden; RunOnceId: "KillAnyPC"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""AnyPC"""; Flags: runhidden; RunOnceId: "DelFirewall"
