  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Management.Infrastructure;
using OmenMon.External;
using OmenMon.Library;

namespace OmenMon.AppCli {

    // Implements the main operation loop in the application's CLI mode
    // This part covers the GPU log: which apps use the discrete GPU, and how much,
    // while another app (TouchDesigner) runs. Measurement only: it reads the Windows
    // GPU performance counters and the GPU's PnP power state; no EC, BIOS, GPU driver
    // (NVML) or configuration access, so it runs unelevated and next to the GUI.
    public static partial class CliOp {

#region GPU Log
        // Logs per process the discrete GPU's engine utilization and dedicated memory every
        // 2 s for the given time (-GpuLog), to a CSV file, and prints a summary per app.
        // Ctrl+C ends it early; the summary is still written
        private static void GpuLogRun(int seconds, string file) {
            if(file == null)
                file = GpuLogMath.DefaultFileName(DateTime.Now);
            file = Path.GetFullPath(file);

            // Find the discrete GPU's LUID from the DXGI adapter list (vendor NVIDIA);
            // without one, every LUID is logged separately rather than guessing
            List<DxgiAdapters.Adapter> adapters = DxgiAdapters.List();
            List<DxgiAdapters.Adapter> discrete = adapters.Where(a => a.VendorId == DxgiAdapters.VendorNvidia && !a.Software).ToList();
            List<string> luids = discrete.Select(a => a.Luid).ToList();
            foreach(DxgiAdapters.Adapter a in adapters)
                Console.WriteLine("Adapter " + a.Luid + ": " + a.Description
                    + (discrete.Contains(a) ? " (discrete GPU)" : ""));
            if(luids.Count == 0)
                Console.WriteLine("No NVIDIA adapter found: all GPUs are logged, each under its LUID");

            string pnpId = luids.Count > 0 ? GpuLogFindPnpId() : null;
            if(luids.Count > 0 && pnpId == null)
                Console.WriteLine("The discrete GPU's PnP entry was not found: its power state is logged as unknown");

            GpuCounters counters;
            StreamWriter writer;
            try {
                counters = new GpuCounters();
            } catch(Exception e) {
                Cli.PrintError("GPU log: " + e.Message);
                Environment.ExitCode = 1;
                return;
            }
            try {
                writer = new StreamWriter(new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            } catch(Exception e) {
                Cli.PrintError("GPU log: cannot write " + file + ": " + e.Message);
                counters.Dispose();
                Environment.ExitCode = 1;
                return;
            }

            ProcessNames names = new ProcessNames();
            GpuLogSummary summary = new GpuLogSummary(GpuLogMath.IntervalSeconds);
            Dictionary<string, int> powerSeen = new Dictionary<string, int>();
            ManualResetEvent wake = new ManualResetEvent(false);
            IsStop = false;
            Console.CancelKeyPress += (sender, eventArgs) => {
                eventArgs.Cancel = true; // End the loop, not the process: the summary follows
                IsStop = true;
                wake.Set();
            };

            Console.WriteLine("Logging to " + file + " for " + seconds + " s (Ctrl+C ends it early)");
            try {
                writer.WriteLine(GpuLogMath.CsvHeader);
                counters.Collect(); // The utilization is a rate: this sample only primes it

                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                int tick = 0;
                while(!IsStop) {

                    // Sleep until the next 2 s mark (or Ctrl+C)
                    tick++;
                    long wait = (long) tick * GpuLogMath.IntervalSeconds * 1000 - clock.ElapsedMilliseconds;
                    if(wait > 0 && wake.WaitOne((int) wait))
                        break;

                    // The PnP power state first (it does not wake the GPU), then the counters
                    string power = pnpId != null ? PowerMath.PowerStateName(CfgMgr.GetDevicePowerState(pnpId)) : "unknown";
                    counters.Collect();
                    List<GpuLogRow> rows = GpuLogMath.Aggregate(DateTime.Now, counters.ReadEngines(), counters.ReadMemory(),
                        luids, names.Get, power);
                    foreach(GpuLogRow r in rows)
                        writer.WriteLine(GpuLogMath.CsvLine(r));
                    writer.Flush();

                    summary.Add(rows);
                    powerSeen.TryGetValue(power, out int n);
                    powerSeen[power] = n + 1;
                    if(tick % 10 == 0)
                        Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  dGPU " + power + ", "
                            + rows.Select(r => r.Pid).Distinct().Count() + " process(es) on the GPU");

                    if(clock.Elapsed.TotalSeconds >= seconds)
                        break;
                }
            } catch(Exception e) {
                Cli.PrintError("GPU log: " + e.Message);
                Environment.ExitCode = 1;
            } finally {
                writer.Dispose();
                counters.Dispose();
            }

            Console.WriteLine();
            Console.Write(summary.Format("dGPU power state: " + (powerSeen.Count == 0 ? "no sample"
                : string.Join(", ", powerSeen.OrderBy(p => p.Key).Select(p => p.Key + " " + p.Value * GpuLogMath.IntervalSeconds + " s")))));
            Console.WriteLine("Log: " + file);
        }

        // The PnP instance ID of the NVIDIA display device, for its power state
        private static string GpuLogFindPnpId() {
            try {
                using(CimSession cim = CimSession.Create(null))
                    foreach(CimInstance d in cim.QueryInstances(@"root\cimv2", "WQL",
                        "SELECT DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Display' AND DeviceID LIKE 'PCI\\\\VEN_10DE%'"))
                        return d.CimInstanceProperties["DeviceID"].Value as string;
            } catch { }
            return null;
        }
#endregion

    }

}
