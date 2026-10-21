; OmenMon-Reborn setup (Inno Setup 6). Built by installer\build-installer.ps1, or by hand:
;   iscc [/DAppVersion=<version>] [/DSource=<folder with the built OmenMon.exe>] installer\OmenMon.iss
;
; Installs for all users to Program Files (only administrators can change files there, which matters because OmenMon
; runs elevated). Optionally starts OmenMon at logon through the scheduled task "OmenMon" (highest rights, command
; line `"<app>\OmenMon.exe" -Run Gui`), exactly the task the tray menu's Autorun toggle creates. The task is created by
; the application itself (`OmenMon.exe -Task Gui=On`), so setup and app can never disagree about how it looks; only if
; that leaves no task behind, setup registers an equivalent one with PowerShell.
;
; OmenMon.xml (the user's settings, next to the exe) is never overwritten by an update and never touched by the
; uninstaller unless the user says yes to the question at its end.
;
; Stopping OmenMon: `OmenMon.exe -Exit 30` asks the running OmenMon of this very folder to exit gracefully (this hands
; the fans back to the BIOS and restores Eco+) and waits up to 30 s. Setup and uninstaller never kill the process;
; should it not exit they stop with a message.
;
; Fan control after OmenMon exits: a fan level set by OmenMon holds about two minutes (~124 s) after the last write,
; then the BIOS curve takes over by itself. There is nothing to release or clean up, so updating or uninstalling
; while the fans are under OmenMon's control is safe.

#ifndef Source
  #define Source "..\Bin"
#endif
#ifndef AppVersion
  #define AppVersion GetVersionNumbersString(AddBackslash(SourcePath) + Source + "\OmenMon.exe")
#endif
#define RepoRoot ".."

[Setup]
; Own AppId, so an installed copy is recognised on updates
AppId={{7D1B5E0C-3A62-4B8F-9C14-0E5A6F2B8D37}
AppName=OmenMon
AppVersion={#AppVersion}
AppVerName=OmenMon {#AppVersion}
AppPublisher=Marius Alwan Meyer (sporqist)
AppPublisherURL=https://github.com/sporqist/OmenMon-Reborn
AppSupportURL=https://github.com/sporqist/OmenMon-Reborn/issues
DefaultDirName={autopf}\OmenMon
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile={#RepoRoot}\LICENSE.md
OutputDir=Output
OutputBaseFilename=OmenMon-Setup-{#AppVersion}
SetupIconFile={#RepoRoot}\Resources\Icon.ico
UninstallDisplayIcon={app}\OmenMon.exe
UninstallDisplayName=OmenMon
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; OmenMon is stopped by the code below (-Exit); the Restart Manager dialog would only confuse
CloseApplications=no
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.TaskAutostart=Start OmenMon with Windows (with administrator rights, through a scheduled task)
en.RunNow=Start OmenMon now
en.ShortcutComment=Fan control and monitoring for HP OMEN and Victus laptops
en.StillRunning=OmenMon is still running and did not exit within 30 seconds.%n%nSetup does not end it by force. Exit OmenMon (tray icon, right-click, Exit) and run setup again.
en.StillRunningUninstall=OmenMon is still running and did not exit within 30 seconds.%n%nThe uninstaller does not end it by force. Exit OmenMon (tray icon, right-click, Exit) and run the uninstaller again.
en.TaskFailed=The scheduled task for starting OmenMon with Windows could not be created. You can turn on Autorun in the OmenMon tray menu later.
en.RestoreFailed=Not everything OmenMon changed in Windows could be put back (see the uninstall log). Check the turbo boost, refresh rate and graphics preferences (Settings, System, Display, Graphics) by hand.
en.RemoveData=Also delete your OmenMon settings and data?%n%nThis removes OmenMon.xml and the OmenMon-*.state and *.log files in:%n%1
de.TaskAutostart=OmenMon mit Windows starten (mit Administratorrechten, über eine geplante Aufgabe)
de.RunNow=OmenMon jetzt starten
de.ShortcutComment=Lüftersteuerung und Überwachung für HP OMEN und Victus Laptops
de.StillRunning=OmenMon läuft noch und wurde innerhalb von 30 Sekunden nicht beendet.%n%nDas Setup beendet es nicht mit Gewalt. Beende OmenMon (Tray-Symbol, Rechtsklick, Exit) und starte das Setup erneut.
de.StillRunningUninstall=OmenMon läuft noch und wurde innerhalb von 30 Sekunden nicht beendet.%n%nDie Deinstallation beendet es nicht mit Gewalt. Beende OmenMon (Tray-Symbol, Rechtsklick, Exit) und starte sie erneut.
de.TaskFailed=Die geplante Aufgabe zum Start von OmenMon mit Windows konnte nicht angelegt werden. Du kannst Autorun später im Tray-Menü von OmenMon einschalten.
de.RestoreFailed=Nicht alles, was OmenMon in Windows geändert hat, konnte zurückgesetzt werden (siehe das Deinstallationsprotokoll). Prüfe Turbo Boost, Bildwiederholrate und Grafikeinstellungen (Einstellungen, System, Anzeige, Grafik) von Hand.
de.RemoveData=Sollen auch deine OmenMon-Einstellungen und -Daten gelöscht werden?%n%nDas entfernt OmenMon.xml sowie die Dateien OmenMon-*.state und *.log in:%n%1

[Icons]
Name: "{autoprograms}\OmenMon"; Filename: "{app}\OmenMon.exe"; WorkingDir: "{app}"; Comment: "{cm:ShortcutComment}"

[Tasks]
; Unchecked by default; on an update it is checked when OmenMon's own logon task already exists (see CurPageChanged)
Name: "autostart"; Description: "{cm:TaskAutostart}"; Flags: unchecked

[Files]
Source: "{#Source}\OmenMon.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Source}\OmenMon.exe.config"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
; The default settings only when there are none yet: never overwritten, never removed by the uninstaller
Source: "{#RepoRoot}\OmenMon.xml"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#RepoRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\LICENSE.md"; DestDir: "{app}"; Flags: ignoreversion

[Run]
; Through the task, so OmenMon runs with the same rights as at logon (no extra admin prompt). Also started on silent
; updates, which brings OmenMon back after it was exited for the update.
Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN OmenMon"; Description: "{cm:RunNow}"; Flags: postinstall nowait runhidden; Check: AutostartWanted
Filename: "{app}\OmenMon.exe"; WorkingDir: "{app}"; Description: "{cm:RunNow}"; Flags: postinstall nowait runasoriginaluser; Check: not AutostartWanted

[Code]
var
  HadOwnTask: Boolean;
  TasksPreselected: Boolean;

function RunHidden(Executable, Parameters: String): Integer;
begin
  if not Exec(Executable, Parameters, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

function PowerShell(Command: String): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Command + '"');
end;

// Whether a task "OmenMon" exists and starts the OmenMon.exe of this folder (the task name is shared by every copy of
// OmenMon, so only a task pointing at this folder is treated as ours)
function HasOwnTask(AppDir: String): Boolean;
begin
  Result := PowerShell('$t = Get-ScheduledTask -TaskName OmenMon -ErrorAction SilentlyContinue; ' +
    'if ($t -and ($t.Actions | Where-Object { $_.Execute.Trim([char]34) -eq ''' + AppDir + '\OmenMon.exe'' })) ' +
    '{ exit 0 } else { exit 1 }') = 0;
end;

// Whether an OmenMon.exe of this folder still runs. Used only to double-check an -Exit that reported failure (an older
// OmenMon.exe without the -Exit command also "fails" while nothing runs).
function IsRunningHere(AppDir: String): Boolean;
begin
  Result := PowerShell('if (Get-Process -Name OmenMon -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq ''' +
    AppDir + '\OmenMon.exe'' }) { exit 1 } else { exit 0 }') <> 0;
end;

// Asks the running OmenMon of this folder to exit gracefully; True when none runs any more. Never kills.
function ExitOmenMon(AppDir: String): Boolean;
var
  Code: Integer;
begin
  Result := True;
  if not FileExists(AppDir + '\OmenMon.exe') then
    Exit;
  Code := RunHidden(AppDir + '\OmenMon.exe', '-Exit 30');
  Log('OmenMon.exe -Exit 30 returned ' + IntToStr(Code));
  if Code <> 0 then
    Result := not IsRunningHere(AppDir);
end;

function AutostartWanted: Boolean;
begin
  // A silent update (no /TASKS given) keeps autostart as it was
  if WizardSilent and (ExpandConstant('{param:TASKS|}') = '') then
    Result := HadOwnTask
  else
    Result := WizardIsTaskSelected('autostart');
end;

procedure InitializeWizard;
begin
  HadOwnTask := False;
  TasksPreselected := False;
end;

// The folder is known from here on, so an update can start with autostart checked when it was on
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
    HadOwnTask := HasOwnTask(WizardDirValue);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectTasks) and not TasksPreselected then
  begin
    TasksPreselected := True;
    if HadOwnTask then
      WizardSelectTasks('autostart');
  end;
end;

// Before any file is replaced
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  AppDir: String;
begin
  Result := '';
  AppDir := ExpandConstant('{app}');
  if WizardSilent then
    HadOwnTask := HasOwnTask(AppDir);
  if not ExitOmenMon(AppDir) then
    Result := CustomMessage('StillRunning');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDir: String;
begin
  if CurStep = ssPostInstall then
  begin
    AppDir := ExpandConstant('{app}');
    if AutostartWanted then
    begin
      // The application creates its own task (the same code as the tray menu's Autorun toggle)
      Log('OmenMon.exe -Task Gui=On returned ' + IntToStr(RunHidden(AppDir + '\OmenMon.exe', '-Task Gui=On')));
      if not HasOwnTask(AppDir) then
      begin
        // Fallback: the same task by hand (logon trigger, highest rights, no battery or time limits)
        PowerShell('$a = New-ScheduledTaskAction -Execute ''' + AppDir + '\OmenMon.exe'' -Argument ''-Run Gui''; ' +
          '$p = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) ' +
          '-LogonType Interactive -RunLevel Highest; ' +
          '$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries ' +
          '-ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable; ' +
          'Register-ScheduledTask -TaskName OmenMon -Action $a -Trigger (New-ScheduledTaskTrigger -AtLogOn) ' +
          '-Principal $p -Settings $s -Force | Out-Null');
        if not HasOwnTask(AppDir) and not WizardSilent then
          MsgBox(CustomMessage('TaskFailed'), mbInformation, MB_OK);
      end;
    end
    else if HadOwnTask then
      // Autostart was on and the user turned it off
      RunHidden(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN OmenMon /F');
  end;
end;

// ---- Uninstall ----

function InitializeUninstall: Boolean;
begin
  Result := True;
  if not ExitOmenMon(ExpandConstant('{app}')) then
  begin
    if not UninstallSilent then
      MsgBox(CustomMessage('StillRunningUninstall'), mbError, MB_OK);
    Result := False;
  end;
end;

// Runs a command line of OmenMon.exe and copies its output into the uninstall log
function RunLogged(AppDir, Arguments: String): Boolean;
var
  LogFile, Output: String;
  AnsiOutput: AnsiString;
  Code: Integer;
begin
  LogFile := ExpandConstant('{tmp}\omenmon-restore.log');
  DeleteFile(LogFile);
  Code := -1;
  Result := Exec(ExpandConstant('{cmd}'), '/C ""' + AppDir + '\OmenMon.exe" ' + Arguments + ' > "' + LogFile + '" 2>&1"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  if LoadStringFromFile(LogFile, AnsiOutput) then
    Output := AnsiOutput
  else
    Output := '(no output)';
  Log('OmenMon.exe ' + Arguments + ' returned ' + IntToStr(Code) + ':' + #13#10 + Output);
end;

procedure DeleteMatching(Pattern: String);
var
  FindRec: TFindRec;
  Dir: String;
begin
  Dir := ExtractFilePath(Pattern);
  if FindFirst(Pattern, FindRec) then
  try
    repeat
      if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0 then
        DeleteFile(Dir + FindRec.Name);
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir: String;
  Ok: Boolean;
begin
  AppDir := ExpandConstant('{app}');
  if CurUninstallStep = usUninstall then
  begin
    // Put back what OmenMon changed in Windows (turbo boost, refresh rate, per-app GPU preferences). OmenMon is not
    // running any more (checked in InitializeUninstall), as these commands require.
    Ok := RunLogged(AppDir, '-EcoPlus Restore');
    Ok := RunLogged(AppDir, '-Graphics Restore') and Ok;
    if not Ok and not UninstallSilent then
      MsgBox(CustomMessage('RestoreFailed'), mbInformation, MB_OK);
    // The logon task, only if it is this install's
    if HasOwnTask(AppDir) then
      RunHidden(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN OmenMon /F');
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    if DirExists(AppDir) and not UninstallSilent then
      if MsgBox(FmtMessage(CustomMessage('RemoveData'), [AppDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DeleteFile(AppDir + '\OmenMon.xml');
        DeleteMatching(AppDir + '\OmenMon-*.state');
        DeleteMatching(AppDir + '\*.log');
        RemoveDir(AppDir);
      end;
  end;
end;
