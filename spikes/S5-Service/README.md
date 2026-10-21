# Spike S5: footprint of an empty NativeAOT service and a WinForms tray

Two halves, both empty: they touch no hardware, make no BIOS/WMI/EC call and open no pipe.

- `S5-Service`: a Windows service as a NativeAOT exe. Raw `advapi32`
  (`StartServiceCtrlDispatcherW`, `RegisterServiceCtrlHandlerExW`, `SetServiceStatus`), one
  control loop that waits 1 s per pass and does nothing (PLAN section 2: one control
  timer, ≥ 1 s). Accepts stop, shutdown and power events. `--console` runs the same loop
  without the SCM.
- `S5-Tray` (folder `..\S5-Tray`): a WinForms `NotifyIcon` with an Exit menu, nothing else.
  WinForms does not support NativeAOT (the SDK stops with NETSDK1175), so it is a
  framework-dependent app on the shared .NET 10 Desktop runtime, the way the tray would
  start out (PLAN section 5: the existing WinForms UI becomes the tray).

## Published sizes (2026-10-21, not run)

| | Size |
|---|---|
| `OmenMon.Spike.Service.exe` (NativeAOT) | 959,488 bytes (0.9 MB), 0 warnings |
| `OmenMon.Spike.Tray.exe` + `.dll` (framework-dependent) | 162,816 + 6,144 bytes, plus the shared runtime |
| Tray forced through NativeAOT anyway (`-p:PublishAot=true -p:_SuppressWinFormsTrimError=true`) | 18,918,400 bytes, with AOT warnings (IL3053 for 4 assemblies, IL3000 ×2): unsupported, not an option |

## How to run (owner's session)

Service, as a real service (elevated; the name is unique to the spike):

```powershell
$exe = (Resolve-Path .\bin\Release\net10.0-windows\win-x64\publish\OmenMon.Spike.Service.exe).Path
sc.exe create OmenCtrlSpikeS5 binPath= "`"$exe`"" start= demand
sc.exe start OmenCtrlSpikeS5
# ... measure ...
sc.exe stop OmenCtrlSpikeS5
sc.exe delete OmenCtrlSpikeS5
```

Or without installing anything: `.\OmenMon.Spike.Service.exe --console` (Ctrl+C ends it).

Tray: start `OmenMon.Spike.Tray.exe` unelevated; exit from the icon's menu.

## What to measure

As in PLAN section 8, after 10 min idle (tray menu closed):

- private bytes, working set, threads and handles of each process
  (`Get-Process OmenMon.Spike.* | Select Name, PrivateMemorySize64, WorkingSet64, @{n='Threads';e={$_.Threads.Count}}, HandleCount`);
- CPU as `\Process(OmenMon.Spike.*)\% Processor Time` over 60 s, divided by the logical
  processor count;
- for the service, both as a service and with `--console` (the console host adds a
  conhost.exe; count only the spike's own process);
- wakeups: `powercfg /energy` for 60 s should not list either process.

Budgets to compare against (PLAN section 2): service ≤ 8 MB private (accept ≤ 20 MB),
≤ 4 threads, ≤ 0.01 % CPU; tray ≤ 30 MB private while only the icon is shown. Baselines:
OmenMon 77.0 MB private / 18 threads, Keykind 18.0 MB / 2 threads.
