  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OmenMon.Library {

    // One performance counter instance of the "GPU Engine" or "GPU Process Memory" set,
    // e.g. pid_1234_luid_0x00000000_0x0000C8E5_phys_0_eng_0_engtype_3D
    public sealed class GpuInstance {

        public int Pid;
        public string Luid;        // "0x00000000_0x0000C8E5", normalized to upper case hex
        public string EngineType;  // "3D", "Compute", "Copy", "VideoDecode", "VideoEncode" or "Other"; null for memory

        // Parses an instance name; false if it is not of this form. The engine type is
        // everything after "engtype_" (it may contain underscores, e.g. Compute_0)
        public static bool TryParse(string name, out GpuInstance instance) {
            instance = null;
            if(name == null || !name.StartsWith("pid_", StringComparison.Ordinal))
                return false;

            // pid_<n>_luid_0x<high>_0x<low>_phys_<n>[_eng_<n>_engtype_<type>]
            string[] part = name.Split('_');
            if(part.Length < 6 || part[2] != "luid")
                return false;
            if(!int.TryParse(part[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                return false;
            if(!TryHex(part[3], out uint high) || !TryHex(part[4], out uint low))
                return false;

            string type = null;
            int at = name.IndexOf("_engtype_", StringComparison.Ordinal);
            if(at >= 0)
                type = NormalizeEngineType(name.Substring(at + "_engtype_".Length));

            instance = new GpuInstance {
                Pid = pid,
                Luid = "0x" + high.ToString("X8") + "_0x" + low.ToString("X8"),
                EngineType = type
            };
            return true;
        }

        private static bool TryHex(string s, out uint value) {
            value = 0;
            return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(s.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        // Maps the driver's engine type names to the few the log reports
        public static string NormalizeEngineType(string raw) {
            if(string.IsNullOrEmpty(raw))
                return "Other";
            if(raw.StartsWith("3D", StringComparison.OrdinalIgnoreCase))
                return "3D";
            if(raw.StartsWith("Compute", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("Cuda", StringComparison.OrdinalIgnoreCase))
                return "Compute";
            if(raw.StartsWith("Copy", StringComparison.OrdinalIgnoreCase))
                return "Copy";
            if(raw.StartsWith("VideoDecode", StringComparison.OrdinalIgnoreCase))
                return "VideoDecode";
            if(raw.StartsWith("VideoEncode", StringComparison.OrdinalIgnoreCase))
                return "VideoEncode";
            return "Other";
        }

        // A DXGI adapter LUID (low part, high part) in the form the counters use
        public static string FormatLuid(uint low, int high) {
            return "0x" + unchecked((uint) high).ToString("X8") + "_0x" + low.ToString("X8");
        }

    }

    // A raw counter reading (instance name, value)
    public struct GpuCounterValue {
        public string Name;
        public double Value;
        public GpuCounterValue(string name, double value) { this.Name = name; this.Value = value; }
    }

    // What is known of a process
    public sealed class GpuProcess {
        public string Name;
        public string Path;
    }

    // One line of the log: one process, one engine type, one GPU, one sample
    public sealed class GpuLogRow {
        public DateTime Time;
        public int Pid;
        public string Process;
        public string Path;
        public string EngineType;   // "-" if the process only holds memory
        public double Utilization;  // [%], summed over the process' engines of this type
        public double DedicatedMB;  // of the process, repeated on each of its rows
        public string PowerState;
        public string Gpu;          // "dGPU", or the LUID if the discrete GPU could not be told apart
    }

    // Aggregation, CSV and summary for the GPU log, kept apart from the PDH readers
    public static class GpuLogMath {

        public static readonly string[] EngineTypes = { "3D", "Compute", "Copy", "VideoDecode", "VideoEncode", "Other" };

        // A process counts as having used an engine from this share [%] on
        public const double ActiveThreshold = 1.0;

        public const string Reference = "TouchDesigner";

        public const int DefaultSeconds = 600;
        public const int IntervalSeconds = 2;

        // Builds the rows of one sample. luids: the discrete GPU's LUID(s), labelled "dGPU";
        // null or empty: every LUID is kept, labelled with itself
        public static List<GpuLogRow> Aggregate(DateTime time, IEnumerable<GpuCounterValue> engines,
            IEnumerable<GpuCounterValue> memory, ICollection<string> luids, Func<int, GpuProcess> resolve, string powerState) {

            // (pid, luid) -> engine type -> utilization; (pid, luid) -> bytes
            var util = new Dictionary<string, Dictionary<string, double>>();
            var bytes = new Dictionary<string, double>();
            var pids = new Dictionary<string, KeyValuePair<int, string>>();

            foreach(GpuCounterValue c in engines ?? new GpuCounterValue[0]) {
                if(!GpuInstance.TryParse(c.Name, out GpuInstance i) || i.EngineType == null || !Wanted(i.Luid, luids))
                    continue;
                string key = i.Pid + "|" + i.Luid;
                pids[key] = new KeyValuePair<int, string>(i.Pid, i.Luid);
                if(!util.TryGetValue(key, out Dictionary<string, double> byType))
                    util[key] = byType = new Dictionary<string, double>();
                byType.TryGetValue(i.EngineType, out double sum);
                byType[i.EngineType] = sum + Math.Max(0, c.Value);
            }
            foreach(GpuCounterValue c in memory ?? new GpuCounterValue[0]) {
                if(!GpuInstance.TryParse(c.Name, out GpuInstance i) || !Wanted(i.Luid, luids))
                    continue;
                string key = i.Pid + "|" + i.Luid;
                pids[key] = new KeyValuePair<int, string>(i.Pid, i.Luid);
                bytes.TryGetValue(key, out double sum);
                bytes[key] = sum + Math.Max(0, c.Value);
            }

            bool single = luids != null && luids.Count > 0;
            List<GpuLogRow> rows = new List<GpuLogRow>();
            foreach(KeyValuePair<string, KeyValuePair<int, string>> p in pids.OrderBy(x => x.Value.Key).ThenBy(x => x.Value.Value)) {
                bytes.TryGetValue(p.Key, out double b);
                double mb = b / (1024.0 * 1024.0);
                GpuProcess proc = resolve != null ? resolve(p.Value.Key) : null;
                string gpu = single ? "dGPU" : p.Value.Value;
                bool any = false;
                if(util.TryGetValue(p.Key, out Dictionary<string, double> byType))
                    foreach(string type in EngineTypes)
                        if(byType.TryGetValue(type, out double u) && (u > 0 || mb > 0)) {
                            rows.Add(Row(time, p.Value.Key, proc, type, u, mb, powerState, gpu));
                            any = true;
                        }
                if(!any && mb > 0)
                    rows.Add(Row(time, p.Value.Key, proc, "-", 0, mb, powerState, gpu));
            }
            return rows;
        }

        private static GpuLogRow Row(DateTime time, int pid, GpuProcess proc, string type, double util, double mb, string power, string gpu) {
            return new GpuLogRow {
                Time = time, Pid = pid,
                Process = proc != null && !string.IsNullOrEmpty(proc.Name) ? proc.Name : "pid " + pid,
                Path = proc != null ? proc.Path : null,
                EngineType = type, Utilization = util, DedicatedMB = mb, PowerState = power, Gpu = gpu
            };
        }

        private static bool Wanted(string luid, ICollection<string> luids) {
            return luids == null || luids.Count == 0 || luids.Contains(luid);
        }

#region CSV
        public const string CsvHeader = "time,pid,process,engine,utilization_percent,dedicated_mb,dgpu_power_state,gpu,path";

        public static string CsvLine(GpuLogRow r) {
            CultureInfo c = CultureInfo.InvariantCulture;
            return string.Join(",", new[] {
                r.Time.ToString("yyyy-MM-dd HH:mm:ss", c), r.Pid.ToString(c), Csv(r.Process), r.EngineType,
                r.Utilization.ToString("0.0", c), r.DedicatedMB.ToString("0.0", c), Csv(r.PowerState), Csv(r.Gpu), Csv(r.Path) });
        }

        // Quotes a field if it holds a comma, a quote or a line break
        public static string Csv(string s) {
            if(string.IsNullOrEmpty(s))
                return "";
            return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
#endregion

        // Reads the arguments of -GpuLog: a whole number of seconds and/or a file name, in
        // either order; false if there are more than one of either or the seconds are not 1 or more
        public static bool TryParseArgs(IList<string> args, out int seconds, out string file) {
            seconds = DefaultSeconds;
            file = null;
            bool haveSeconds = false;
            foreach(string a in args) {
                if(a.Length > 0 && a.All(ch => ch >= '0' && ch <= '9')) {
                    if(haveSeconds || !int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds < 1)
                        return false;
                    haveSeconds = true;
                } else if(file == null)
                    file = a;
                else
                    return false;
            }
            return true;
        }

        public static string DefaultFileName(DateTime time) {
            return "OmenMon-GpuLog-" + time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv";
        }

        public static bool IsReference(string process) {
            return process != null && process.StartsWith(Reference, StringComparison.OrdinalIgnoreCase);
        }
    }

    // The per-app totals of a run
    public sealed class GpuLogSummary {

        public sealed class App {
            public string Name;
            public string Path;
            public Dictionary<string, double> Sum = new Dictionary<string, double>();   // per engine type, over all samples
            public Dictionary<string, double> Peak = new Dictionary<string, double>();
            public double PeakMB;
            public int ActiveSamples;   // samples with any engine at or above the threshold
            public int MemorySamples;   // samples with any dedicated memory

            public double Average(string type, int samples) {
                return samples > 0 && this.Sum.TryGetValue(type, out double s) ? s / samples : 0;
            }
            public double PeakOf(string type) {
                return this.Peak.TryGetValue(type, out double p) ? p : 0;
            }
            // Peak of the busiest engine type
            public double PeakAny { get { return this.Peak.Count == 0 ? 0 : this.Peak.Values.Max(); } }
        }

        public int Samples { get; private set; }
        public double IntervalSeconds;
        public Dictionary<string, App> Apps = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);

        public GpuLogSummary(double intervalSeconds) { this.IntervalSeconds = intervalSeconds; }

        // Adds one sample (the rows of one call of GpuLogMath.Aggregate; may be empty).
        // Processes of the same name are one app
        public void Add(IEnumerable<GpuLogRow> rows) {
            this.Samples++;
            foreach(IGrouping<string, GpuLogRow> g in rows.GroupBy(r => r.Process, StringComparer.OrdinalIgnoreCase)) {
                if(!this.Apps.TryGetValue(g.Key, out App app))
                    this.Apps[g.Key] = app = new App { Name = g.Key };
                if(app.Path == null)
                    app.Path = g.Select(r => r.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p));

                // Per engine type over the app's processes; memory once per pid
                Dictionary<string, double> now = new Dictionary<string, double>();
                foreach(GpuLogRow r in g)
                    if(r.EngineType != "-") {
                        now.TryGetValue(r.EngineType, out double u);
                        now[r.EngineType] = u + r.Utilization;
                    }
                double mb = g.GroupBy(r => r.Pid).Sum(p => p.Max(r => r.DedicatedMB));

                foreach(KeyValuePair<string, double> kv in now) {
                    app.Sum.TryGetValue(kv.Key, out double s);
                    app.Sum[kv.Key] = s + kv.Value;
                    if(!app.Peak.TryGetValue(kv.Key, out double p) || kv.Value > p)
                        app.Peak[kv.Key] = kv.Value;
                }
                if(now.Values.Any(v => v >= GpuLogMath.ActiveThreshold))
                    app.ActiveSamples++;
                if(mb > 0)
                    app.MemorySamples++;
                app.PeakMB = Math.Max(app.PeakMB, mb);
            }
        }

        // Whether an app other than the reference used the discrete GPU
        public static bool Flagged(App a) {
            return !GpuLogMath.IsReference(a.Name) && (a.PeakAny >= GpuLogMath.ActiveThreshold || a.PeakMB >= 1.0);
        }

        // The text printed at the end
        public string Format(string note) {
            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("GPU log summary: " + this.Samples + " sample(s) of " + this.IntervalSeconds.ToString("0.#", c)
                + " s" + (note != null ? "; " + note : ""));
            sb.AppendLine("Per engine: average over the whole run / peak [%]; busy = seconds with an engine at or above "
                + GpuLogMath.ActiveThreshold.ToString("0", c) + " %; * = used the GPU and is not " + GpuLogMath.Reference);

            foreach(App a in this.Apps.Values.OrderByDescending(x => x.Sum.Values.Sum()).ThenByDescending(x => x.PeakMB).ThenBy(x => x.Name)) {
                sb.Append(Flagged(a) ? "* " : "  ").Append(a.Name);
                if(!string.IsNullOrEmpty(a.Path))
                    sb.Append("  (").Append(a.Path).Append(')');
                sb.AppendLine();
                List<string> parts = new List<string>();
                foreach(string type in GpuLogMath.EngineTypes)
                    if(a.Peak.ContainsKey(type))
                        parts.Add(type + " " + a.Average(type, this.Samples).ToString("0.0", c) + "/" + a.PeakOf(type).ToString("0.0", c));
                sb.Append("    ").Append(parts.Count > 0 ? string.Join(", ", parts) : "no engine time")
                  .Append("; VRAM peak ").Append(a.PeakMB.ToString("0", c)).Append(" MB")
                  .Append("; busy ").Append((a.ActiveSamples * this.IntervalSeconds).ToString("0", c)).AppendLine(" s");
            }
            if(this.Apps.Count == 0)
                sb.AppendLine("  no process used the GPU");

            List<App> flagged = this.Apps.Values.Where(Flagged).OrderBy(x => x.Name).ToList();
            sb.AppendLine();
            if(flagged.Count > 0)
                sb.AppendLine("Apps other than " + GpuLogMath.Reference + " that used the discrete GPU: " + string.Join(", ", flagged.Select(x => x.Name)));
            else
                sb.AppendLine("No app other than " + GpuLogMath.Reference + " used the discrete GPU.");
            return sb.ToString();
        }

    }

}
