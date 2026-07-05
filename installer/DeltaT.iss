; DeltaT installer — Inno Setup script.
; Build with installer\build-installer.ps1 (it publishes the app, then calls ISCC
; with the right /D defines). Don't run ISCC on this file directly unless you pass
; /DAppVersion and have a populated .\publish folder beside it.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "publish"
#endif

#define AppName "DeltaT"
#define AppPublisher "Azaan Murtaza"
#define AppExe "DeltaT.App.exe"
#define AppTaskName "DeltaT"

[Setup]
; A stable GUID keeps upgrades/uninstalls tied to the same product across versions.
AppId={{7B3D5C2A-1E9F-4A6B-9D2C-DE17A7C0FFEE}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=out
OutputBaseFilename=DeltaT-Setup-{#AppVersion}
SetupIconFile=..\src\DeltaT.App\Assets\deltat.ico
; Program Files + the scheduled task both need elevation; installer prompts UAC once.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; We terminate a running DeltaT ourselves (see PrepareToInstall) — the tray app
; ignores WM_CLOSE (close-to-tray), so Restart Manager can't unlock its files.
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional shortcuts:"
Name: "startup"; Description: "Start {#AppName} automatically when I sign in (recommended — it runs quietly in the tray)"; GroupDescription: "Startup:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Register the logon auto-start task (elevated where possible, battery-safe) if opted in.
; Group principal (BUILTIN\Users, S-1-5-32-545) + RunLevel Highest = starts in the
; signed-in user's session, elevated for admins (no UAC prompt), silent for others.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$a = New-ScheduledTaskAction -Execute '{app}\{#AppExe}' -Argument '--minimized'; $t = New-ScheduledTaskTrigger -AtLogOn; $p = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Highest; $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable; Register-ScheduledTask -TaskName '{#AppTaskName}' -Action $a -Trigger $t -Principal $p -Settings $s -Force"""; \
  Flags: runhidden waituntilterminated; Tasks: startup; \
  StatusMsg: "Registering startup task..."
; Offer to launch straight away (into the tray).
Filename: "{app}\{#AppExe}"; Parameters: "--minimized"; \
  Description: "Launch {#AppName} now"; \
  Flags: postinstall nowait skipifsilent

[UninstallRun]
; Drop the scheduled task on uninstall (harmless if it was never created).
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Unregister-ScheduledTask -TaskName '{#AppTaskName}' -Confirm:$false -ErrorAction SilentlyContinue"""; \
  Flags: runhidden; RunOnceId: "DelDeltaTTask"

[UninstallDelete]
; Runtime data (learned thermal history, settings) stays in %LOCALAPPDATA%\DeltaT
; so a reinstall keeps the machine's baseline. Nothing to delete from {app}.

[Code]
// The tray app ignores WM_CLOSE, and self-contained files stay locked while it
// runs, so force-terminate it (and stop its task) before copying new files.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "Stop-ScheduledTask -TaskName ''{#AppTaskName}'' -ErrorAction SilentlyContinue; Get-Process -Name ''DeltaT.App'' -ErrorAction SilentlyContinue | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
