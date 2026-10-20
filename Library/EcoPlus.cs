  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
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
    //    2 min, 18 times in a day, turned every drop into two black screens. Only the
    //    internal panel is changed, at most once per battery period, and the original is
    //    put back only on that panel and only while it still runs at the rate Eco+ set:
    //    a rate the user or a preset chose since is left as it is.
    //
    // Per-app GPU preferences were part of Eco+ until 2026-10-20; they are now their own
    // feature, Graphics apps (GraphicsApps.cs), which took over the originals Eco+ kept
    // (GraphicsMigration).
    //
    // The original values live in a small state file, so they survive restarts and a
    // crash between changing and restoring a value. Each original is saved there before
    // its value is changed; a value whose original cannot be saved is not changed.
    public sealed class EcoPlus {

        // Values written
        public const uint BoostDisabled = 0;

        private readonly IPowerPlan plan;
        private readonly IDisplayRate display;
        private readonly EcoPlusState state;
        private readonly Func<EcoPlusOptions> options;
        private readonly Func<DateTime> clock;
        private readonly object sync = new object();

        // Power source as last seen, and since when (for the refresh-rate hold)
        private bool? lineOnBattery;
        private DateTime lineSince;

        public EcoPlus(IPowerPlan plan, IDisplayRate display,
            EcoPlusState state, Func<EcoPlusOptions> options, Func<DateTime> clock = null) {
            this.plan = plan;
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

        // The refresh rate is decided once per power-source period (from one change of
        // the power source to the next, or until the options change): Eco+ lowers it at
        // most once, and a rate that is no longer what Eco+ set belongs to whoever set it
        // (the user, a preset). Only when Windows itself put the saved mode back (after
        // a resume or a display being plugged in or out, see NoteDisplayReset) is it
        // lowered again, once per period.
        private bool refreshDecided, refreshReapplied;
        private DateTime? displayResetAt;
        private const int DisplayResetSeconds = 600;
        private int refreshRestoreFailures;
        private bool? lastEnabled;
        private int lastRefreshRate = -1;

        private void NewPeriod() {
            this.refreshDecided = false;
            this.refreshReapplied = false;
        }

        private bool DisplayWasReset(DateTime now, int holdSeconds) {
            return this.displayResetAt.HasValue && (now - this.displayResetAt.Value).TotalSeconds <= DisplayResetSeconds + Math.Max(0, holdSeconds);
        }

        // Windows may have put the displays' saved modes back (resume from sleep, a
        // display plugged in or out); a dynamic rate change does not survive that
        public void NoteDisplayReset() {
            lock(this.sync)
                this.displayResetAt = this.clock();
        }

        // The user set the refresh rate (a preset or the tray menu, saved as the display's
        // mode): it is theirs, even if it happens to be the rate Eco+ set, so it is not
        // put back and not lowered again in this period
        public void NoteRefreshSetByUser() {
            lock(this.sync) {
                this.refreshDecided = true;
                if(!this.state.RefreshBefore.HasValue)
                    return;
                Log("refresh set by the user: not put back to " + this.state.RefreshBefore.Value + " Hz");
                ForgetRefresh();
                SaveRestored();
            }
        }

        // Brings the settings in line with the options and the power source. Called on
        // start, on a power source change and periodically (the user may switch power
        // schemes). Returns a short description of what changed, or null.
        public string Maintain(bool onBattery) {
            lock(this.sync) {
                if(this.stopped)
                    return null;
                EcoPlusOptions o = this.options();
                List<string> done = new List<string>();

                DateTime now = this.clock();
                if(this.lineOnBattery != onBattery) {
                    bool first = !this.lineOnBattery.HasValue;
                    this.lineOnBattery = onBattery;
                    this.lineSince = now;
                    NewPeriod();
                    // A rate Eco+ set before this start may have been reset by a reboot
                    if(first)
                        this.displayResetAt = now;
                }
                if(this.lastEnabled != o.Enabled || this.lastRefreshRate != o.RefreshRateOnBattery) {
                    if(this.lastEnabled.HasValue)
                        NewPeriod();
                    this.lastEnabled = o.Enabled;
                    this.lastRefreshRate = o.RefreshRateOnBattery;
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
                if(onBattery && o.RefreshRateOnBattery > 0)
                    LowerRefresh(o.RefreshRateOnBattery, o.RefreshHoldSeconds, stable, now, done);
                else if(this.state.RefreshBefore.HasValue) {
                    // Plugged in (and stable), or the option was turned off: put the rate back
                    if(o.RefreshRateOnBattery > 0 && !stable)
                        RefreshPending = true;
                    else
                        RestoreRefresh(done);
                }

                return done.Count > 0 ? string.Join(", ", done) : null;
            }
        }

        // The display the saved rate belongs to (a state file from before display
        // identities were kept names none: the panel then)
        private string RefreshDisplay() {
            return this.state.RefreshDisplay ?? this.display.Panel();
        }

        // Whether a display's rate is still the one Eco+ set (unknown for a state file
        // from before that was kept: then anything below the original)
        private bool IsEcoPlusRate(int current) {
            return this.state.RefreshSet > 0 ? current == this.state.RefreshSet : current < this.state.RefreshBefore.GetValueOrDefault();
        }

        private string SetRateText() {
            return this.state.RefreshSet > 0 ? this.state.RefreshSet + " Hz" : "lower";
        }

        private void ForgetRefresh() {
            this.state.RefreshBefore = null;
            this.state.RefreshSet = 0;
            this.state.RefreshDisplay = null;
            this.refreshRestoreFailures = 0;
        }

        private void LowerRefresh(int target, int holdSeconds, bool stable, DateTime now, List<string> done) {

            // A rate Eco+ set earlier, in this period or before a restart
            if(this.state.RefreshBefore.HasValue) {
                string id = RefreshDisplay();
                int current = id != null ? this.display.Current(id) : 0;
                if(current <= 0) {
                    // That display is off or gone; if it comes back, it is in its saved mode
                    this.displayResetAt = now;
                    return;
                }
                if(IsEcoPlusRate(current)) {
                    this.refreshDecided = true;
                    return;
                }
                if(current == this.state.RefreshBefore.Value && DisplayWasReset(now, holdSeconds) && !this.refreshReapplied) {
                    // Windows put the saved mode back: lowered again, once per period
                    if(!stable) {
                        RefreshPending = true;
                        return;
                    }
                    this.refreshReapplied = true;
                    this.refreshDecided = true;
                    this.displayResetAt = null;
                    if(this.display.SetDynamic(id, target)) {
                        if(this.state.RefreshSet != target) {
                            this.state.RefreshSet = target;
                            SaveRestored();
                        }
                        done.Add("refresh " + current + " -> " + target + " Hz again (Windows had put " + current + " Hz back)");
                    }
                    return;
                }
                // Someone else set this rate: it is theirs, left alone until the power source changes
                done.Add("refresh now " + current + " Hz, not the " + SetRateText() + " Eco+ set: left as it is");
                ForgetRefresh();
                SaveRestored();
                this.refreshDecided = true;
                return;
            }

            if(this.refreshDecided)
                return;
            string panel = this.display.Panel();
            int rate = panel != null ? this.display.Current(panel) : 0;
            if(rate <= 0)
                return;   // no internal panel active (lid closed): nothing to lower
            if(rate <= target) {
                if(stable)
                    this.refreshDecided = true;
                return;
            }
            if(!stable) {
                RefreshPending = true;
                return;
            }
            this.refreshDecided = true;

            // The original, the rate set and the display, on disk before the change
            this.state.RefreshBefore = rate;
            this.state.RefreshSet = target;
            this.state.RefreshDisplay = panel;
            if(!Save("the refresh rate")) {
                ForgetRefresh();
                return;
            }
            if(this.display.SetDynamic(panel, target))
                done.Add("refresh " + rate + " -> " + target + " Hz");
            else {
                ForgetRefresh();
                SaveRestored();
            }
        }

        // Puts the saved rate back, but only on the display it came from and only while
        // that display still runs at the rate Eco+ set; otherwise the entry is dropped
        private void RestoreRefresh(List<string> done) {
            int before = this.state.RefreshBefore.Value;
            string id = RefreshDisplay();
            int current = id != null ? this.display.Current(id) : 0;
            if(current <= 0)
                done.Add("refresh not put back to " + before + " Hz: the display Eco+ changed is not active");
            else if(!IsEcoPlusRate(current))
                done.Add("refresh left at " + current + " Hz, not the " + SetRateText() + " Eco+ set: not put back to " + before + " Hz");
            else if(this.display.SetDynamic(id, before))
                done.Add("refresh back to " + before + " Hz");
            else if(++this.refreshRestoreFailures >= 3)
                done.Add("refresh could not be put back to " + before + " Hz: given up");
            else
                return;   // tried again on the next call
            ForgetRefresh();
            SaveRestored();
        }

        // OmenMon exits (or the Windows session ends): the battery settings are put back
        // and Eco+ changes nothing more in this run. The next start applies them again.
        public string RestoreForExit() {
            lock(this.sync) {
                this.stopped = true;
                return RestoreSettingsLocked();
            }
        }

        private bool stopped;

        // Everything Eco+ changed (OmenMon -EcoPlus Restore, for uninstalling); a
        // description of what was done, or null if nothing was saved. GPU preferences
        // are not Eco+'s any more: -Graphics Restore puts those back.
        public string RestoreAll() {
            lock(this.sync) {
                this.stopped = true;
                return RestoreSettingsLocked();
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
            NewPeriod();
            bool boostChanged = false;
            foreach(KeyValuePair<Guid, uint> saved in this.state.SavedBoostDc.ToList()) {
                // Only while the scheme still has the value Eco+ wrote (it may have been
                // changed with powercfg, or the scheme deleted, while Eco+ was off)
                uint? current = this.plan.ReadBoostDc(saved.Key);
                if(current == BoostDisabled) {
                    this.plan.WriteBoostDc(saved.Key, saved.Value);
                    done.Add("boost on battery back to " + saved.Value);
                } else
                    done.Add("boost on battery left at " + WindowsPowerPlan.BoostModeName(current) + ", not put back to " + saved.Value
                        + (current.HasValue ? " (changed since Eco+ set it)" : " (scheme gone)"));
                this.state.SavedBoostDc.Remove(saved.Key);
                boostChanged = true;
            }
            if(boostChanged)
                SaveRestored();
            if(this.state.RefreshBefore.HasValue)
                RestoreRefresh(done);
            return done.Count > 0 ? string.Join(", ", done) : null;
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
    // next to the state file (see ChangeLog)
    public static class EcoPlusLog {

        public const string FileName = "OmenMon-EcoPlus.log";
        public const int MaxBytes = ChangeLog.MaxBytes;

        public static void Append(string path, DateTime time, string text) {
            ChangeLog.Append(path, time, text);
        }

    }

    // The original values Eco+ will restore. Text file, one value per line:
    //   boost <TAB> {scheme GUID} <TAB> value
    //   refresh <TAB> original Hz <TAB> Hz Eco+ set <TAB> display
    //     (the display as IDisplayRate names it; files from before a display was
    //     kept have only the original Hz)
    //   gpu <TAB> app <TAB> original value (empty = the app had no preference)
    //     (from before Graphics apps took GPU preferences over: read so that
    //     GraphicsMigration can move them, and written back only while they are
    //     still here, so a failed move loses none; Eco+ never adds one)
    //
    // Saved before Eco+ changes Windows, with the robustness of StateFile.
    public sealed class EcoPlusState : StateFile {

        public const string FileName = "OmenMon-EcoPlus.state";

        public readonly Dictionary<Guid, uint> SavedBoostDc = new Dictionary<Guid, uint>();
        public readonly Dictionary<string, string> SavedGpu = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int? RefreshBefore;
        public int RefreshSet;              // the rate Eco+ set, 0 = not known
        public string RefreshDisplay;       // the display it was set on, null = not known

        public EcoPlusState(string path) : base(path) { }

        protected override string Owner { get { return "Eco+"; } }
        protected override string LogFileName { get { return EcoPlusLog.FileName; } }

        public static EcoPlusState Load(string path) {
            EcoPlusState s = new EcoPlusState(path);
            s.LoadFile();
            return s;
        }

        protected override void Parse(string[] lines) {
            this.SavedBoostDc.Clear();
            this.SavedGpu.Clear();
            this.RefreshBefore = null;
            this.RefreshSet = 0;
            this.RefreshDisplay = null;
            foreach(string line in lines) {
                string[] f = line.Split('\t');
                if(f.Length == 3 && f[0] == "boost" && Guid.TryParse(f[1], out Guid g) && uint.TryParse(f[2], out uint v))
                    this.SavedBoostDc[g] = v;
                else if((f.Length == 2 || f.Length == 4) && f[0] == "refresh" && int.TryParse(f[1], out int hz) && hz > 0) {
                    this.RefreshBefore = hz;
                    if(f.Length == 4 && int.TryParse(f[2], out int set) && set > 0 && f[3].Length > 0) {
                        this.RefreshSet = set;
                        this.RefreshDisplay = f[3];
                    }
                }
                else if(f.Length == 3 && f[0] == "gpu" && f[1].Length > 0)
                    this.SavedGpu[f[1]] = f[2].Length > 0 ? f[2] : null;
            }
        }

        public override bool IsEmpty {
            get { return this.SavedBoostDc.Count == 0 && this.SavedGpu.Count == 0 && !this.RefreshBefore.HasValue; }
        }

        protected override string Format() {
            StringBuilder sb = new StringBuilder();
            foreach(KeyValuePair<Guid, uint> b in this.SavedBoostDc)
                sb.Append("boost\t").Append(b.Key.ToString("B")).Append('\t').Append(b.Value).Append("\r\n");
            if(this.RefreshBefore.HasValue)
                sb.Append("refresh\t").Append(this.RefreshBefore.Value).Append('\t').Append(this.RefreshSet)
                  .Append('\t').Append(this.RefreshDisplay ?? "").Append("\r\n");
            foreach(KeyValuePair<string, string> g in this.SavedGpu)
                sb.Append("gpu\t").Append(g.Key).Append('\t').Append(g.Value ?? "").Append("\r\n");
            return sb.ToString();
        }

    }

    // Seams (real implementations in EcoPlusWindows.cs, fakes in the tests)
    public interface IPowerPlan {
        Guid ActiveScheme();                    // Guid.Empty if unknown
        uint? ReadBoostDc(Guid scheme);
        void WriteBoostDc(Guid scheme, uint value);   // and re-applies the scheme if active
    }

    // Per-app GPU preferences (used by Graphics apps, see GraphicsApps.cs)
    public interface IGpuPreferences {
        IList<string> Apps();                   // every value name in the store, empty if none
        string Get(string app);                 // null if none (or not a string)
        void Set(string app, string value);
        void Remove(string app);
    }

    public interface IDisplayRate {
        string Panel();                         // the display Eco+ changes (the internal panel), null if none is active
        int Current(string display);            // its rate now, 0 if that display is not active
        bool SetDynamic(string display, int hz);   // not saved as the display's mode
    }

}
