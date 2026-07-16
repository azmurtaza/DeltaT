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

; PawnIO: the signed kernel driver DeltaT reads CPU thermal registers through.
; LibreHardwareMonitor 0.9.5+ ships no driver of its own, so this is a hard runtime
; dependency for CPU temperature, package power, TjMax and throttle detection. It is
; downloaded from the author's official GitHub release and its Authenticode signature is
; verified against the publisher below BEFORE it is executed, so a hijacked mirror or a
; tampered download cannot get a kernel driver installed through us.
#define PawnIoUrl "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe"
#define PawnIoSigner "CN=namazso.eu"
; The service LibreHardwareMonitor 0.9.4 (WinRing0) used to register. DeltaT no longer
; loads it, but an upgrade must actively remove what the old build left behind: while that
; driver is installed, its ioctl remains a local privilege-escalation primitive
; (CVE-2020-14979) and kernel anti-cheats still refuse to run.
#define LegacyRing0Service "WinRing0_1_2_0"

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
Name: "startup"; Description: "Start {#AppName} automatically when I sign in (recommended, it runs quietly in the tray)"; GroupDescription: "Startup:"
; Only offered when PawnIO isn't already present (FanControl, OpenRGB and LibreHardwareMonitor
; users often have it). Unchecking it is allowed: DeltaT still runs, with CPU temperature from
; the slower motherboard/ACPI reading and no package power, throttle events or headroom.
Name: "pawnio"; Description: "Install the PawnIO sensor driver (needed to read CPU temperature)"; GroupDescription: "Sensor driver:"; Check: not PawnIoInstalled

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
; Offer to launch straight away. No --minimized: someone who just ticked "launch now"
; wants to SEE the app, not hunt for a tray icon. The logon task above keeps --minimized,
; where starting quietly is the point.
Filename: "{app}\{#AppExe}"; \
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
var
  DownloadPage: TDownloadWizardPage;

// PawnIO's own installer drops PawnIOLib.dll here and registers an uninstall entry.
// Either signal is enough: a user who already runs FanControl, OpenRGB or
// LibreHardwareMonitor very likely has it, and we must not reinstall over them.
function PawnIoInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf}\PawnIO\PawnIOLib.dll'))
         or RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO');
end;

// Never execute a downloaded binary we haven't authenticated. This is a KERNEL DRIVER
// installer: a hijacked mirror, a stale CDN entry or a tampered file would be handing
// ring0 to an attacker. Authenticode (rather than a pinned hash) is what actually proves
// authorship, and it keeps working when the pinned release URL is bumped.
function SignatureIsTrusted(const FileName: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -Command "$s = Get-AuthenticodeSignature ''' + FileName + '''; ' +
      'if ($s.Status -ne ''Valid'') { exit 1 }; ' +
      'if ($s.SignerCertificate.Subject -notlike ''*{#PawnIoSigner}*'') { exit 2 }; exit 0"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and (ResultCode = 0);
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

// Fetch the driver installer before the install step, so a failed download is a clean
// "carry on without it" rather than a half-installed app.
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsTaskSelected('pawnio') then
  begin
    DownloadPage.Clear;
    // Empty hash: the Authenticode check above is the gate, and it survives a version bump.
    DownloadPage.Add('{#PawnIoUrl}', 'PawnIO_setup.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        // Offline, or GitHub unreachable. Not fatal — DeltaT runs without the driver and
        // Settings offers to install it later.
        SuppressibleMsgBox('The PawnIO sensor driver could not be downloaded, so CPU temperature '
          + 'will be unavailable for now. DeltaT will install fine, and you can add the driver '
          + 'later from Settings.', mbInformation, MB_OK, IDOK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

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

procedure CurStepChanged(CurStep: TSetupStep);
var
  SetupExe: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  // 1. Retire WinRing0. Versions of DeltaT up to 2.1.0 shipped LibreHardwareMonitor 0.9.4,
  // which registers this driver service on first run. Leaving it behind would keep the
  // vulnerable driver on the machine (and Battlefield 6 / Valorant unlaunchable) even though
  // the new build never touches it. Harmless when it was never there.
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#LegacyRing0Service}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#LegacyRing0Service}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  DeleteFile(ExpandConstant('{app}\WinRing0x64.sys'));
  DeleteFile(ExpandConstant('{app}\WinRing0.sys'));

  // 2. Install PawnIO, but only after proving who signed it.
  if not WizardIsTaskSelected('pawnio') then
    Exit;
  SetupExe := ExpandConstant('{tmp}\PawnIO_setup.exe');
  if not FileExists(SetupExe) then
    Exit; // download failed earlier; already reported

  if not SignatureIsTrusted(SetupExe) then
  begin
    SuppressibleMsgBox('The downloaded PawnIO installer is not signed by its author, so DeltaT did '
      + 'not run it. This can mean the download was tampered with. CPU temperature will be '
      + 'unavailable; you can install the driver yourself from https://pawnio.eu.',
      mbCriticalError, MB_OK, IDOK);
    Exit;
  end;

  if not Exec(SetupExe, '-install -silent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    SuppressibleMsgBox('The PawnIO sensor driver did not install cleanly, so CPU temperature may be '
      + 'unavailable. You can retry from Settings, or install it from https://pawnio.eu.',
      mbInformation, MB_OK, IDOK);
  end;
end;
