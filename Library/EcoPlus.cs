  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OmenMon.Library {

    // Eco+: battery-only efficiency settings, each one reversible.
    //
    //  * Turbo boost off on battery: the active power scheme's DC boost value is set to 0
    //    (Disabled). The AC value is never touched, so plugged-in performance is the same.
    //    The original DC value is kept per scheme and written back when Eco+ is turned
    //    off. Measured on 8BAC (2026-10-16, WSL agents running): 59.5 W -> 39.2 W.
    //  * Optional refresh rate on battery, applied as a dynamic mode change (not saved as
    //    the display's mode) and put back on AC or when Eco+ is turned off. Each change
    //    blanks the panel for about a second, so it waits until the power source has been
    //    stable for RefreshHoldSeconds: on 8BAC a train socket dropping out for 5 s to
    //    2 min, 18 times in a day, turned every drop into two black screens.
    //  * Per-app GPU preference "power saving" (HKCU DirectX UserGpuPreferences) for the
    //    apps that keep the discrete GPU awake. Explicit, not tied to battery: the apps
    //    read it when they start. The original values are kept and can be restored.
    //
    // The original values live in a small state file, so they survive restarts and a
    // crash between changing and restoring a value. Each original is saved there before
    // its value is changed; a value whose original cannot be saved is not changed.
    public sealed class EcoPlus {

        // Values written
        public const uint BoostDisabled = 0;
        public const string GpuPowerSaving = "GpuPreference=1;";

        private readonly IPowerPlan plan;
        private readonly IGpuPreferences gpu;
        private readonly IDisplayRate display;
        private readonly EcoPlusState state;
        private readonly Func<EcoPlusOptions> options;
        private readonly Func<DateTime> clock;
        private readonly object sync = new object();

        // Power source as last seen, and since when (for the refresh-rate hold)
        private bool? lineOnBattery;
        private DateTime lineSince;

        public EcoPlus(IPowerPlan plan, IGpuPreferences gpu, IDisplayRate display,
            EcoPlusState state, Func<EcoPlusOptions> options, Func<DateTime> clock = null) {
            this.plan = plan;
            this.gpu = gpu;
            this.display = display;
            this.state = state;
            this.options = options;
            this.clock = clock ?? (() => DateTime.UtcNow);
            if(state.LoadError != null)
                Log(state.LoadError);
        }

        public EcoPlusState State { get { return this.state; } }

        // Why the last change was not made (the originals could not be saved), or null
        public string LastError { get; private set; }

        // Errors go to the Eco+ log directly; what changed is returned to the caller
        private void Log(string text) {
            EcoPlusLog.Append(this.state.LogPath, DateTime.Now, "Eco+: " + text);
        }

        // Saves the state file; on failure the caller does not make its change. Logged
        // once per run of failures, not on every periodic call.
        private bool Save(string what) {
            if(this.state.Save()) {
                this.saveFailing = false;
                return true;
            }
            this.LastError = "could not save the original value, so " + what + " was not changed: " + this.state.LastError;
            if(!this.saveFailing)
                Log(this.LastError);
            this.saveFailing = true;
            return false;
        }

        private bool saveFailing;

        // Saves the state file after a restore. The value is back either way; if the file
        // still lists it, a later restore finds it no longer as Eco+ set it and drops it.
        private void SaveRestored() {
            if(!this.state.Save())
                Log("restored, but could not update the state file: " + this.state.LastError);
        }

        // True while a refresh-rate change waits for the power source to be stable, so
        // the caller keeps calling Maintain until it is decided
        public bool RefreshPending { get; private set; }

        // Brings the settings in line with the options and the power source. Called on
        // start, on a power source change and periodically (the user may switch power
        // schemes). Returns a short description of what changed, or null.
        public string Maintain(bool onBattery) {
            lock(this.sync) {
                EcoPlusOptions o = this.options();
                List<string> done = new List<string>();

                DateTime now = this.clock();
                if(this.lineOnBattery != onBattery) {
                    this.lineOnBattery = onBattery;
                    this.lineSince = now;
                }
                bool stable = (now - this.lineSince).TotalSeconds >= Math.Max(0, o.RefreshHoldSeconds);
                RefreshPending = false;
                this.LastError = null;

                // While the saved originals cannot be read, nothing is changed or restored
                if(!this.state.Reload())
                    return null;

                if(!o.Enabled) {
                    string restored = RestoreSettingsLocked();
                    return restored;
                }

                // Turbo boost off on battery, for whichever scheme is active now
                Guid scheme = this.plan.ActiveScheme();
                if(scheme != Guid.Empty) {
                    uint? dc = this.plan.ReadBoostDc(scheme);
                    if(dc.HasValue && dc.Value != BoostDisabled) {
                        bool saved = this.state.SavedBoostDc.ContainsKey(scheme);
                        if(!saved) {
                            // The original is on disk before the change, or there is no change
                            this.state.SavedBoostDc[scheme] = dc.Value;
                            saved = Save("turbo boost");
                            if(!saved)
                                this.state.SavedBoostDc.Remove(scheme);
                        }
                        if(saved) {
                            this.plan.WriteBoostDc(scheme, BoostDisabled);
                            done.Add("boost off on battery (was " + dc.Value + ")");
                        }
                    }
                }

                // Refresh rate on battery, once the power source has been stable
                if(onBattery && o.RefreshRateOnBattery > 0) {
                    int current = this.display.Current();
                    if(current > o.RefreshRateOnBattery) {
                        if(!stable)
                            RefreshPending = true;
                        else {
                            bool fresh = !this.state.RefreshBefore.HasValue;
                            if(fresh) {
                                this.state.RefreshBefore = current;
                                if(!Save("the refresh rate")) {
                                    this.state.RefreshBefore = null;
                                    return done.Count > 0 ? string.Join(", ", done) : null;
                                }
                            }
                            if(this.display.SetDynamic(o.RefreshRateOnBattery))
                                done.Add("refresh " + current + " -> " + o.RefreshRateOnBattery + " Hz");
                            else if(fresh) {
                                this.state.RefreshBefore = null;
                                Save("the refresh rate");
                            }
                        }
                    }
                } else if(this.state.RefreshBefore.HasValue) {
                    // Plugged in (and stable), or the option was turned off: put the rate back
                    if(o.RefreshRateOnBattery > 0 && !stable) {
                        RefreshPending = true;
                        return done.Count > 0 ? string.Join(", ", done) : null;
                    }
                    int before = this.state.RefreshBefore.Value;
                    if(this.display.SetDynamic(before)) {
                        this.state.RefreshBefore = null;
                        SaveRestored();
                        done.Add("refresh back to " + before + " Hz");
                    }
                }

                return done.Count > 0 ? string.Join(", ", done) : null;
            }
        }

        // Writes back every boost value and the refresh rate Eco+ changed
        public string RestoreSettings() {
            lock(this.sync)
                return RestoreSettingsLocked();
        }

        private string RestoreSettingsLocked() {
            List<string> done = new List<string>();
            if(!this.state.Reload())
                return null;
            foreach(KeyValuePair<Guid, uint> saved in this.state.SavedBoostDc.ToList()) {
                this.plan.WriteBoostDc(saved.Key, saved.Value);
                this.state.SavedBoostDc.Remove(saved.Key);
                done.Add("boost on battery back to " + saved.Value);
            }
            if(this.state.RefreshBefore.HasValue && this.display.SetDynamic(this.state.RefreshBefore.Value)) {
                done.Add("refresh back to " + this.state.RefreshBefore.Value + " Hz");
                this.state.RefreshBefore = null;
            }
            if(done.Count > 0)
                SaveRestored();
            return done.Count > 0 ? string.Join(", ", done) : null;
        }

        // Sets the given apps (exe path or packaged-app AUMID) to "power saving", keeping
        // each app's original value the first time; returns how many were changed
        public int ReleaseGpu(IEnumerable<string> apps) {
            lock(this.sync) {
                this.LastError = null;
                int changed = 0;
                if(!this.state.Reload()) {
                    this.LastError = "the saved originals cannot be read, so nothing was changed";
                    return 0;
                }
                foreach(string app in apps.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase)) {
                    string current = this.gpu.Get(app);
                    if(current == GpuPowerSaving)
                        continue;
                    // Each original is on disk before its app is changed; on a failed save
                    // this app and the rest are left as they are
                    if(!this.state.SavedGpu.ContainsKey(app)) {
                        this.state.SavedGpu[app] = current;   // null = had no preference
                        if(!Save("the GPU preference of " + app)) {
                            this.state.SavedGpu.Remove(app);
                            break;
                        }
                    }
                    this.gpu.Set(app, GpuPowerSaving);
                    changed++;
                }
                return changed;
            }
        }

        // Puts every GPU preference Eco+ changed back as it was; returns how many
        public int RestoreGpu() {
            lock(this.sync) {
                int restored = 0;
                if(!this.state.Reload())
                    return 0;
                foreach(KeyValuePair<string, string> saved in this.state.SavedGpu.ToList()) {
                    if(saved.Value == null)
                        this.gpu.Remove(saved.Key);
                    else
                        this.gpu.Set(saved.Key, saved.Value);
                    this.state.SavedGpu.Remove(saved.Key);
                    restored++;
                }
                if(restored > 0)
                    SaveRestored();
                return restored;
            }
        }

    }

    public sealed class EcoPlusOptions {
        public bool Enabled;
        public int RefreshRateOnBattery;   // 0 = leave the refresh rate alone
        public int RefreshHoldSeconds = 120;   // power source stable this long before a refresh change

        public static EcoPlusOptions FromConfig() {
            return new EcoPlusOptions {
                Enabled = Config.EcoPlusEnabled,
                RefreshRateOnBattery = Config.EcoPlusRefreshRate,
                RefreshHoldSeconds = Config.EcoPlusRefreshHold };
        }
    }

    // What Eco+ changed and when: one timestamped line per change in OmenMon-EcoPlus.log
    // next to the state file. Cut to its newer half when it passes MaxBytes.
    public static class EcoPlusLog {

        public const string FileName = "OmenMon-EcoPlus.log";
        public const int MaxBytes = 64 * 1024;

        public static void Append(string path, DateTime time, string text) {
            if(path == null || string.IsNullOrEmpty(text))
                return;
            try {
                if(File.Exists(path) && new FileInfo(path).Length > MaxBytes) {
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    File.WriteAllLines(path, lines.Skip(lines.Length / 2), new UTF8Encoding(false));
                }
                File.AppendAllText(path, time.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + text + "\r\n", new UTF8Encoding(false));
            } catch {
                // The log is a record, not a safeguard: a line that cannot be written
                // never stops or undoes a change (the state file is what keeps originals)
            }
        }

    }

    // The original values Eco+ will restore. Text file, one value per line:
    //   boost <TAB> {scheme GUID} <TAB> value
    //   refresh <TAB> Hz
    //   gpu <TAB> app <TAB> original value (empty = the app had no preference)
    //
    // Eco+ saves it before it changes Windows, and changes nothing if the save fails.
    // A file that exists but cannot be read is never overwritten: it is moved aside to
    // a .bad copy (originals in it kept for a human), or, if even that fails, Save
    // refuses until a later Reload can read it.
    public sealed class EcoPlusState {

        public const string FileName = "OmenMon-EcoPlus.state";

        public readonly Dictionary<Guid, uint> SavedBoostDc = new Dictionary<Guid, uint>();
        public readonly Dictionary<string, string> SavedGpu = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int? RefreshBefore;

        private readonly string path;

        public EcoPlusState(string path) {
            this.path = path;
        }

        // Why the file could not be loaded (null if it was, or there was none)
        public string LoadError { get; private set; }

        // True while an unreadable file is still in place: Save does not overwrite it
        public bool Blocked { get; private set; }

        // Why the last Save failed (null if it worked)
        public string LastError { get; private set; }

        // The Eco+ log next to the state file
        public string LogPath {
            get { return this.path == null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(this.path)), EcoPlusLog.FileName); }
        }

        private string TempPath { get { return this.path + ".tmp"; } }

        public static EcoPlusState Load(string path) {
            EcoPlusState s = new EcoPlusState(path);
            string source = s.Source();
            if(source == null)
                return s;
            try {
                s.Parse(File.ReadAllLines(source, Encoding.UTF8));
            } catch(Exception e) {
                s.SetAside(source, e);
            }
            return s;
        }

        // Reads the file again while Blocked; true once it is no longer blocked
        public bool Reload() {
            if(!this.Blocked)
                return true;
            try {
                string source = Source();
                if(source != null)
                    Parse(File.ReadAllLines(source, Encoding.UTF8));
                this.Blocked = false;
                this.LoadError = null;
                return true;
            } catch {
                return false;
            }
        }

        // The file to read: the state file, or the temporary file of a save that was cut
        // short between writing it and putting it in place
        private string Source() {
            if(this.path == null)
                return null;
            if(File.Exists(this.path))
                return this.path;
            return File.Exists(TempPath) ? TempPath : null;
        }

        private void Parse(string[] lines) {
            this.SavedBoostDc.Clear();
            this.SavedGpu.Clear();
            this.RefreshBefore = null;
            foreach(string line in lines) {
                string[] f = line.Split('\t');
                if(f.Length == 3 && f[0] == "boost" && Guid.TryParse(f[1], out Guid g) && uint.TryParse(f[2], out uint v))
                    this.SavedBoostDc[g] = v;
                else if(f.Length == 2 && f[0] == "refresh" && int.TryParse(f[1], out int hz) && hz > 0)
                    this.RefreshBefore = hz;
                else if(f.Length == 3 && f[0] == "gpu" && f[1].Length > 0)
                    this.SavedGpu[f[1]] = f[2].Length > 0 ? f[2] : null;
            }
        }

        private void SetAside(string source, Exception e) {
            this.LoadError = "could not read " + Path.GetFileName(source) + " (" + e.Message + ")";
            string bad = this.path + ".bad";
            if(File.Exists(bad))
                bad = this.path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try {
                File.Move(source, bad);
                this.LoadError += ", kept as " + Path.GetFileName(bad) + " with the originals it holds; starting empty";
            } catch {
                this.Blocked = true;
                this.LoadError += " and could not move it aside: Eco+ changes nothing until it can be read";
            }
        }

        public bool IsEmpty {
            get { return this.SavedBoostDc.Count == 0 && this.SavedGpu.Count == 0 && !this.RefreshBefore.HasValue; }
        }

        // Atomic: written to a temporary file first, which is never deleted while it may
        // be the only copy; the file is removed when empty. False if it was not saved.
        public bool Save() {
            if(this.path == null)
                return true;
            if(this.Blocked) {
                this.LastError = "the state file could not be read, so it is not overwritten";
                return false;
            }
            try {
                if(IsEmpty) {
                    if(File.Exists(this.path))
                        File.Delete(this.path);
                    if(File.Exists(TempPath))
                        File.Delete(TempPath);
                    this.LastError = null;
                    return true;
                }
                StringBuilder sb = new StringBuilder();
                foreach(KeyValuePair<Guid, uint> b in this.SavedBoostDc)
                    sb.Append("boost\t").Append(b.Key.ToString("B")).Append('\t').Append(b.Value).Append("\r\n");
                if(this.RefreshBefore.HasValue)
                    sb.Append("refresh\t").Append(this.RefreshBefore.Value).Append("\r\n");
                foreach(KeyValuePair<string, string> g in this.SavedGpu)
                    sb.Append("gpu\t").Append(g.Key).Append('\t').Append(g.Value ?? "").Append("\r\n");
                File.WriteAllText(TempPath, sb.ToString(), new UTF8Encoding(false));
                if(File.Exists(this.path)) {
                    try {
                        File.Replace(TempPath, this.path, null);
                    } catch when(!File.Exists(this.path) && File.Exists(TempPath)) {
                        // ReplaceFile can fail after moving the old file away: the new
                        // content is in the temporary file, so put that in place
                        File.Move(TempPath, this.path);
                    }
                } else
                    File.Move(TempPath, this.path);
                this.LastError = null;
                return true;
            } catch(Exception e) {
                this.LastError = e.Message;
                return false;
            }
        }

    }

    // Seams (real implementations in EcoPlusWindows.cs, fakes in the tests)
    public interface IPowerPlan {
        Guid ActiveScheme();                    // Guid.Empty if unknown
        uint? ReadBoostDc(Guid scheme);
        void WriteBoostDc(Guid scheme, uint value);   // and re-applies the scheme if active
    }

    public interface IGpuPreferences {
        string Get(string app);                 // null if none
        void Set(string app, string value);
        void Remove(string app);
    }

    public interface IDisplayRate {
        int Current();
        bool SetDynamic(int hz);                // not saved as the display's mode
    }

}
