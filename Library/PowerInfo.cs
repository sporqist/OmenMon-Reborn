  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Management.Infrastructure;
using OmenMon.External;

namespace OmenMon.Library {

    // One reading for the Power & Efficiency window
    public sealed class PowerSnapshot {

        public DateTime Time;

        // Battery (root\wmi): capacities [mWh], rates [mW], voltage [mV]
        public bool? OnAc;
        public int? RemainingMWh, FullMWh, DesignMWh, DischargeMW, ChargeMW, VoltageMV;

        // CPU: load [%] and performance relative to base clock [%] (> 100 = boosting)
        public double? CpuLoad, CpuPerformance;

        // Power scheme and its processor boost setting
        public Guid Scheme;
        public string SchemeName;
        public uint? BoostAc, BoostDc;

        // Display
        public int RefreshHz;

        // Discrete GPU: PnP power state (1 = D0 on ... 4 = D3 off, -1 unknown) and, only
        // while it is on, NVML board power [mW], performance state and temperature
        public string GpuName;
        public int GpuPowerState = -1;
        public uint? GpuPowerMW, GpuTemperature;
        public int? GpuPState;
        public List<GpuHolder> GpuHolders = new List<GpuHolder>();
        public DateTime? GpuDetailsTime;   // when the NVML values above were read

        public bool OnBattery { get { return this.OnAc == false; } }

    }

    // A process holding a context on the discrete GPU
    public sealed class GpuHolder {
        public int Pid;
        public string Name;          // process name
        public string Path;          // executable path, if readable
        public string AppKey;        // GPU preference key (AUMID or path)
        public string Preference;    // current GPU preference value (null = default)
    }

    // Calculations kept apart from the readers, so they can be tested
    public static class PowerMath {

        // Time left at the current discharge rate; null if not discharging or unknown
        public static TimeSpan? Runtime(int? remainingMWh, int? dischargeMW) {
            if(!remainingMWh.HasValue || !dischargeMW.HasValue || dischargeMW.Value <= 0 || remainingMWh.Value < 0)
                return null;
            return TimeSpan.FromHours((double) remainingMWh.Value / dischargeMW.Value);
        }

        // The gauge reports the full-charge capacity for minutes after unplugging (seen on
        // 8BAC: 63980 of 63980 mWh for 3 min at 50+ W), so the remaining capacity is not
        // a measurement then; the discharge rate still is
        public static bool IsGaugePinned(PowerSnapshot s) {
            return s.OnBattery && s.RemainingMWh.HasValue && s.FullMWh.HasValue
                && s.RemainingMWh.Value >= s.FullMWh.Value && s.DischargeMW.GetValueOrDefault() > 0;
        }

        public static string PowerStateName(int state) {
            switch(state) {
                case 1: return "D0 (on)";
                case 2: return "D1";
                case 3: return "D2";
                case 4: return "D3 (off)";
                default: return "unknown";
            }
        }

        public static string Watts(int? milliwatts) {
            return milliwatts.HasValue ? (milliwatts.Value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " W" : "?";
        }

        public static string Duration(TimeSpan? t) {
            if(!t.HasValue) return "?";
            return (int) t.Value.TotalHours + " h " + t.Value.Minutes.ToString("00") + " min";
        }

        // The text shown in the window
        public static string Format(PowerSnapshot s) {
            StringBuilder sb = new StringBuilder();
            CultureInfo c = CultureInfo.InvariantCulture;

            sb.AppendLine("POWER SOURCE");
            if(s.OnAc.HasValue) {
                sb.Append("  ").AppendLine(s.OnAc.Value ? "plugged in" : "on battery");
                if(s.OnBattery) {
                    sb.Append("  draw          ").AppendLine(Watts(s.DischargeMW));
                    sb.Append("  time left     ").Append(Duration(Runtime(s.RemainingMWh, s.DischargeMW)));
                    sb.AppendLine(IsGaugePinned(s) ? "  (gauge still at full charge, real time is shorter)" : "");
                } else if(s.ChargeMW.GetValueOrDefault() > 0)
                    sb.Append("  charging      ").AppendLine(Watts(s.ChargeMW));
                if(s.RemainingMWh.HasValue && s.FullMWh.HasValue && s.FullMWh.Value > 0)
                    sb.Append("  battery       ").Append(s.RemainingMWh.Value.ToString("N0", c)).Append(" of ")
                      .Append(s.FullMWh.Value.ToString("N0", c)).Append(" mWh (")
                      .Append((100.0 * s.RemainingMWh.Value / s.FullMWh.Value).ToString("0", c)).AppendLine(" %)");
                if(s.FullMWh.HasValue && s.DesignMWh.GetValueOrDefault() > 0)
                    sb.Append("  wear          full charge ").Append((100.0 * s.FullMWh.Value / s.DesignMWh.Value).ToString("0", c))
                      .Append(" % of design (").Append(s.DesignMWh.Value.ToString("N0", c)).AppendLine(" mWh)");
            } else
                sb.AppendLine("  no battery data");

            sb.AppendLine().AppendLine("CPU");
            sb.Append("  load          ").AppendLine(s.CpuLoad.HasValue ? s.CpuLoad.Value.ToString("0", c) + " %" : "?");
            sb.Append("  clock         ").Append(s.CpuPerformance.HasValue ? s.CpuPerformance.Value.ToString("0", c) + " % of base" : "?")
              .AppendLine(s.CpuPerformance.GetValueOrDefault() > 100 ? "  (boosting)" : "");
            sb.Append("  power scheme  ").AppendLine(s.SchemeName ?? "?");
            sb.Append("  turbo boost   plugged in: ").Append(WindowsPowerPlan.BoostModeName(s.BoostAc))
              .Append(", on battery: ").AppendLine(WindowsPowerPlan.BoostModeName(s.BoostDc));

            sb.AppendLine().AppendLine("DISPLAY");
            sb.Append("  refresh       ").Append(s.RefreshHz).AppendLine(" Hz");

            sb.AppendLine().Append("DISCRETE GPU").AppendLine(s.GpuName != null ? "  " + s.GpuName : "");
            sb.Append("  state         ").AppendLine(PowerStateName(s.GpuPowerState));
            if(s.GpuPowerState == 1) {
                int age = s.GpuDetailsTime.HasValue ? (int) (s.Time - s.GpuDetailsTime.Value).TotalSeconds : 0;
                sb.Append("  power         ").Append(s.GpuPowerMW.HasValue ? Watts((int) s.GpuPowerMW.Value) : "?")
                  .Append(s.GpuPState.HasValue ? ", P" + s.GpuPState.Value : "")
                  .Append(s.GpuTemperature.HasValue ? ", " + s.GpuTemperature.Value + " °C" : "")
                  .AppendLine(age >= 5 ? "  (read " + age + " s ago)" : "");
                if(s.GpuHolders.Count > 0) {
                    sb.AppendLine("  kept awake by (apps with a context on it):");
                    foreach(GpuHolder h in s.GpuHolders)
                        sb.Append("    ").Append(h.Name).Append(" (").Append(h.Pid).Append(")  preference: ")
                          .AppendLine(RegistryGpuPreferences.Describe(h.Preference));
                } else
                    sb.AppendLine("  no app holds it: it can power off when idle");
            } else if(s.GpuPowerState == 4)
                sb.AppendLine("  asleep: drawing no power");

            return sb.ToString();
        }

    }

    // When the Power window reads NVML. Each NVML session talks to the GPU, which can
    // keep it from powering off (runtime D3), so it is read only while the GPU is on
    // (D0) anyway: once when it has just woken up (to show what woke it), then every
    // BusyInterval while apps hold it, and only every IdleInterval while none does,
    // when it could power off. In between the window shows the last values.
    public sealed class NvmlSchedule {

        public static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);

        private DateTime? last;
        private bool lastIdle, woke;
        private int lastState = -1;

        // Whether to read now, given the PnP power state (1 = D0)
        public bool Due(DateTime now, int powerState) {
            if(powerState == 1 && this.lastState != 1)
                this.woke = true;   // until the next reading
            this.lastState = powerState;
            if(powerState != 1)
                return false;
            if(!this.last.HasValue)
                return true;
            TimeSpan since = now - this.last.Value;
            return since >= (this.lastIdle && !this.woke ? IdleInterval : BusyInterval);
        }

        // A reading was made, with this many apps holding the GPU
        public void Done(DateTime now, int holders) {
            this.last = now;
            this.lastIdle = holders == 0;
            this.woke = false;
        }

    }

    // Reads PowerSnapshots. Keeps its sessions open between readings; not thread-safe,
    // use from one thread (the window's worker).
    public sealed class PowerReader : IDisposable {

        private CimSession cim;
        private PerformanceCounter cpuLoad, cpuPerf;
        private readonly WindowsPowerPlan plan = new WindowsPowerPlan();
        private readonly RegistryGpuPreferences prefs = new RegistryGpuPreferences();
        private string gpuInstanceId;
        private bool gpuLookedUp, nvmlUp, nvmlFailed;
        private IntPtr nvmlDevice;
        private readonly NvmlSchedule nvmlSchedule = new NvmlSchedule();
        private PowerSnapshot nvmlLast;   // the last NVML values, shown until the next reading

        public PowerSnapshot Read() {
            PowerSnapshot s = new PowerSnapshot { Time = DateTime.Now };
            ReadBattery(s);
            ReadCpu(s);
            try {
                s.Scheme = this.plan.ActiveScheme();
                if(s.Scheme != Guid.Empty) {
                    s.SchemeName = this.plan.FriendlyName(s.Scheme);
                    s.BoostAc = this.plan.ReadBoostAc(s.Scheme);
                    s.BoostDc = this.plan.ReadBoostDc(s.Scheme);
                }
            } catch { }
            try { s.RefreshHz = Os.GetRefreshRate(); } catch { }
            ReadGpu(s);
            return s;
        }

        // The discrete GPU only (the Graphics apps window): its PnP power state and,
        // only while it is on, the apps holding it (NVML, as for the Power window)
        public PowerSnapshot ReadGpuOnly() {
            PowerSnapshot s = new PowerSnapshot { Time = DateTime.Now };
            ReadGpu(s);
            return s;
        }

        private CimSession Cim {
            get {
                if(this.cim == null)
                    try { this.cim = CimSession.Create(null); } catch { }
                return this.cim;
            }
        }

        private CimInstance First(string ns, string cls) {
            try {
                return Cim?.QueryInstances(ns, "WQL", "SELECT * FROM " + cls).FirstOrDefault();
            } catch {
                return null;
            }
        }

        private static int? Int(CimInstance i, string property) {
            try {
                object v = i?.CimInstanceProperties[property]?.Value;
                return v == null ? (int?) null : Convert.ToInt32(v);
            } catch {
                return null;
            }
        }

        private void ReadBattery(PowerSnapshot s) {
            CimInstance status = First(@"root\wmi", "BatteryStatus");
            if(status != null) {
                try { s.OnAc = (bool) status.CimInstanceProperties["PowerOnline"].Value; } catch { }
                s.RemainingMWh = Int(status, "RemainingCapacity");
                s.DischargeMW = Int(status, "DischargeRate");
                s.ChargeMW = Int(status, "ChargeRate");
                s.VoltageMV = Int(status, "Voltage");
            }
            s.FullMWh = Int(First(@"root\wmi", "BatteryFullChargedCapacity"), "FullChargedCapacity");
            s.DesignMWh = Int(First(@"root\wmi", "BatteryStaticData"), "DesignedCapacity");
        }

        private void ReadCpu(PowerSnapshot s) {
            try {
                if(this.cpuLoad == null) {
                    this.cpuLoad = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                    this.cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                    // Rate counters need two samples: the first call only primes them
                    this.cpuLoad.NextValue();
                    this.cpuPerf.NextValue();
                    return;
                }
                s.CpuLoad = this.cpuLoad.NextValue();
                s.CpuPerformance = this.cpuPerf.NextValue();
            } catch { }
        }

        private void ReadGpu(PowerSnapshot s) {
            if(!this.gpuLookedUp) {
                this.gpuLookedUp = true;
                try {
                    CimInstance d = Cim?.QueryInstances(@"root\cimv2", "WQL",
                        "SELECT DeviceID, Name FROM Win32_PnPEntity WHERE PNPClass = 'Display' AND DeviceID LIKE 'PCI\\\\VEN_10DE%'").FirstOrDefault();
                    if(d != null) {
                        this.gpuInstanceId = d.CimInstanceProperties["DeviceID"].Value as string;
                        s.GpuName = d.CimInstanceProperties["Name"].Value as string;
                        this.gpuName = s.GpuName;
                    }
                } catch { }
            }
            s.GpuName = this.gpuName;
            if(this.gpuInstanceId == null)
                return;

            // PnP power state first: NVML would wake a GPU that is asleep
            s.GpuPowerState = CfgMgr.GetDevicePowerState(this.gpuInstanceId);
            bool due = this.nvmlSchedule.Due(s.Time, s.GpuPowerState);
            if(s.GpuPowerState != 1 || this.nvmlFailed)
                return;
            if(!due || CfgMgr.GetDevicePowerState(this.gpuInstanceId) != 1) {   // again, right before
                ShowLast(s);
                return;
            }
            // A failed reading waits the idle interval too, not 3 s
            this.nvmlSchedule.Done(s.Time, 0);
            // NVML is opened and closed around each reading: an open session could itself
            // keep the GPU from powering off
            try {
                if(Nvml.Init() != Nvml.NVML_SUCCESS) {
                    this.nvmlFailed = true;
                    return;
                }
                this.nvmlUp = true;
                if(Nvml.DeviceGetHandleByIndex(0, out this.nvmlDevice) != Nvml.NVML_SUCCESS)
                    return;
                if(Nvml.DeviceGetPowerUsage(this.nvmlDevice, out uint mw) == Nvml.NVML_SUCCESS) s.GpuPowerMW = mw;
                if(Nvml.DeviceGetPerformanceState(this.nvmlDevice, out int p) == Nvml.NVML_SUCCESS && p < 32) s.GpuPState = p;
                if(Nvml.DeviceGetTemperature(this.nvmlDevice, Nvml.NVML_TEMPERATURE_GPU, out uint t) == Nvml.NVML_SUCCESS) s.GpuTemperature = t;
                HashSet<uint> pids = new HashSet<uint>();
                CollectPids(Nvml.DeviceGetComputeRunningProcesses, pids);
                CollectPids(Nvml.DeviceGetGraphicsRunningProcesses, pids);
                foreach(uint pid in pids.OrderBy(x => x)) {
                    GpuHolder h = new GpuHolder { Pid = (int) pid };
                    try { h.Name = Process.GetProcessById((int) pid).ProcessName; } catch { h.Name = "?"; }
                    try {
                        AppIdentity.Resolve((int) pid, out h.AppKey, out h.Path);
                        if(h.AppKey != null) h.Preference = this.prefs.Get(h.AppKey);
                    } catch { }
                    s.GpuHolders.Add(h);
                }
                s.GpuDetailsTime = s.Time;
                this.nvmlSchedule.Done(s.Time, s.GpuHolders.Count);
                this.nvmlLast = s;
            } catch(DllNotFoundException) {
                this.nvmlFailed = true;
            } catch(EntryPointNotFoundException) {
                this.nvmlFailed = true;
            } catch {
            } finally {
                if(this.nvmlUp) {
                    try { Nvml.Shutdown(); } catch { }
                    this.nvmlUp = false;
                }
            }
        }

        private string gpuName;

        // The last NVML values, with each app's GPU preference read again (cheap, and it
        // changes when the window sets apps to power saving)
        private void ShowLast(PowerSnapshot s) {
            PowerSnapshot l = this.nvmlLast;
            if(l == null)
                return;
            s.GpuPowerMW = l.GpuPowerMW;
            s.GpuPState = l.GpuPState;
            s.GpuTemperature = l.GpuTemperature;
            s.GpuDetailsTime = l.GpuDetailsTime;
            foreach(GpuHolder h in l.GpuHolders) {
                GpuHolder c = new GpuHolder { Pid = h.Pid, Name = h.Name, Path = h.Path, AppKey = h.AppKey, Preference = h.Preference };
                try {
                    if(c.AppKey != null) c.Preference = this.prefs.Get(c.AppKey);
                } catch { }
                s.GpuHolders.Add(c);
            }
        }

        private delegate int ProcessQuery(IntPtr device, ref uint count, Nvml.ProcessInfo[] infos);

        private void CollectPids(ProcessQuery query, HashSet<uint> pids) {
            uint count = 64;
            Nvml.ProcessInfo[] infos = new Nvml.ProcessInfo[count];
            int r = query(this.nvmlDevice, ref count, infos);
            if(r == Nvml.NVML_ERROR_INSUFFICIENT_SIZE) {
                infos = new Nvml.ProcessInfo[count];
                r = query(this.nvmlDevice, ref count, infos);
            }
            if(r != Nvml.NVML_SUCCESS)
                return;
            for(int i = 0; i < count && i < infos.Length; i++)
                pids.Add(infos[i].Pid);
        }

        public void Dispose() {
            try { if(this.nvmlUp) Nvml.Shutdown(); } catch { }
            this.nvmlUp = false;
            this.cpuLoad?.Dispose();
            this.cpuPerf?.Dispose();
            this.cim?.Dispose();
        }

    }

}
