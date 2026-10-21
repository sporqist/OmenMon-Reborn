# Spike S1: BIOS WMI reads from a NativeAOT exe

**Read-only.** The program sends two kinds of call, both reads, and nothing else:

| Call | Input | Output | Meaning |
|---|---|---|---|
| command 0x20008, type 0x2D | `00 00 00 00` | 128 bytes (`hpqBIOSInt128`) | measured fan levels `[cpu, gpu]`, rpm/100 |
| command 0x20008, type 0x23 | `01 00 00 00` | 4 bytes (`hpqBIOSInt4`) | sensor 1 = CPU temperature (EC 0xB2), °C |

The inputs and output sizes are the ones `BiosCtl.GetFanLevel` and `BiosCtl.GetTemperature`
use today. Every call goes through `ReadBios()`, which refuses any other type before
anything is sent; `SpikeTripwireTests` checks the source for that.

## What it shows

The service in PLAN section 5 has to call WMI without `System.Management` (not
AOT-compatible). This spike does it with plain COM: `IWbemLocator::ConnectServer`
(`ROOT\WMI`), the `hpqBIntM` instance path from a WQL query, an `hpqBDataIn` instance
(`Sign` = `SECU`, `Command`, `CommandType`, `Size`, `hpqBData`) and
`IWbemServices::ExecMethod` on `hpqBIOSInt<N>`. Vtable calls through function pointers,
blittable P/Invokes only, `DisableRuntimeMarshalling`: no COM interop marshalling and no
package (CsWin32 would generate the same declarations; it is not needed for two calls).

Publish result (2026-10-21): builds with **0 AOT/trim warnings** (warnings are errors in
this project), `OmenMon.Spike.WmiRead.exe` 1,540,608 bytes. **Not run yet.**

## How to run (owner's session)

1. Publish (see `..\README.md` for the `vswhere` PATH note).
2. In an **elevated** console (the manifest asks for elevation):

   ```powershell
   .\OmenMon.Spike.WmiRead.exe            # one sample
   .\OmenMon.Spike.WmiRead.exe 600 1000   # 600 samples, 1 s apart (10 min)
   ```

   The installed OmenMon keeps control of the fans; the spike only adds two reads per sample.

Expected output: one `connect:` line with the time and the `hpqBIntM` instance path, one
line per sample (`fans 23/26 (rpm/100, rc 0, 2.1 ms)  cpu 52 C (rc 0, 1.8 ms)`), then the
process's private bytes, working set and thread count.

## What to measure

- **Works at all:** rc 0 for both calls; fan levels and CPU temperature match OmenMon's
  window (CPU = its "BIOS" sensor) at the same moment.
- **Latency:** the per-call times (OmenMon's CIM path for comparison: the same reads in
  its `-Bios` CLI). Interesting are the first call after connect and the steady state.
- **Footprint:** the closing `process:` line after the 600-sample run (private bytes and
  threads), against PLAN section 2 (service target ≤ 8 MB private, ≤ 4 threads). Also
  `Get-Process OmenMon.Spike.WmiRead | Select PrivateMemorySize64, Threads` during the run.
- **Leaks:** private bytes at sample 10 vs. sample 600 (each call creates and releases
  five COM objects; a leak would show here first).
- **Failure shape:** run it once unelevated on purpose (`runas /trustlevel:0x20000`): it
  should end with a readable `ConnectServer`/`ExecQuery` access-denied message, not a
  crash.
