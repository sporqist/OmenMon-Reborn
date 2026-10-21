# Spike S5, tray half: a WinForms tray icon

The smallest WinForms tray app: a `NotifyIcon` with an Exit menu. No hardware, no pipe, no
global names. Framework-dependent (WinForms does not support NativeAOT), published with:

```powershell
dotnet publish spikes\S5-Tray\OmenMon.Spike.Tray.csproj -c Release
```

`OmenMon.Spike.Tray.exe` (162,816 bytes) + `.dll` (6,144 bytes) on the shared .NET 10
Desktop runtime. **Not run yet.** How to run it and what to measure (private bytes while
only the icon is shown, against the tray budget of ≤ 30 MB) is in `..\S5-Service\README.md`,
together with the service half.
