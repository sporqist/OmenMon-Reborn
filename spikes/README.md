# Restructuring spikes (phase 0)

Throw-away programs that answer one question each for the restructuring plan
(`PLAN.md`, section 7, phase 0). They are **not** in `OmenMon.sln`, the main project
excludes this folder from its globs, and nothing here is installed or started by a build.

| Spike | Question | Folder |
|---|---|---|
| S1 | Can a NativeAOT exe read the BIOS WMI interface (`root\wmi hpqBIntM`, `SECU`) through plain COM, without `System.Management`? | `S1-WmiRead` |
| S5 | What does an empty NativeAOT service cost, and what does a WinForms tray icon cost? | `S5-Service`, `S5-Tray` |

S2 (GPU temperature without the EC) is not here yet.

## Building

NativeAOT needs the MSVC linker (Visual Studio "Desktop development with C++" or the
Build Tools). On this machine it is there (VS 2022 and VS 18 Community), but the
ILCompiler targets call `vswhere.exe` by name, and the VS Installer folder is not on
`PATH`. Without it, the link step fails with *"'vswhere.exe' is not recognized"*. Put it on
`PATH` for the publishing shell only:

```powershell
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;" + $env:PATH
dotnet publish spikes\S1-WmiRead\OmenMon.Spike.WmiRead.csproj -c Release
dotnet publish spikes\S5-Service\OmenMon.Spike.Service.csproj -c Release
dotnet publish spikes\S5-Tray\OmenMon.Spike.Tray.csproj -c Release
```

The output is in each folder's `bin\Release\net10.0-windows\win-x64\publish\`.

Sizes published on 2026-10-21 (SDK 10.0.402, ILCompiler 10.0.13):

| Exe | Size | Kind |
|---|---|---|
| `OmenMon.Spike.WmiRead.exe` | 1,540,608 bytes (1.5 MB) | NativeAOT, self-contained |
| `OmenMon.Spike.Service.exe` | 959,488 bytes (0.9 MB) | NativeAOT, self-contained |
| `OmenMon.Spike.Tray.exe` + `.dll` | 162,816 + 6,144 bytes | framework-dependent (needs the .NET 10 Desktop runtime) |

## Rules

- Running any of them is the owner's call, in the owner's session. S1 reads the real BIOS
  and needs elevation; S5's service has to be registered with `sc.exe`.
- S1 is read-only and must stay so: `SpikeTripwireTests` fails if its source names any
  type other than the two reads.
- Global names (service, pipe, mutex) are unique to the spikes, never one an OmenMon
  copy uses.
