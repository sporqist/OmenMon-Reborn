  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OmenMon.Library {

    // Graphics apps: applies a profile's GPU preferences (see Graphics.cs for the model
    // and the planner) and keeps the original of every app it changes in a state file,
    // so that Windows default can give each one back, after restarts and crashes too.
    //
    // Each original is on disk before its app's value is changed; if it cannot be saved,
    // that app and the rest are left as they are. A value changed outside OmenMon is
    // never overwritten (the user's choice wins). Every change is logged to
    // OmenMon-Graphics.log. Apps read their preference when they start: Apply returns
    // the apps whose GPU changes, and restarting them is up to the caller.
    public sealed class GraphicsApps {

        private readonly IGpuPreferences gpu;
        private readonly GraphicsState state;
        private readonly object sync = new object();

        public GraphicsApps(IGpuPreferences gpu, GraphicsState state) {
            this.gpu = gpu;
            this.state = state;
            if(state.LoadError != null)
                Log(state.LoadError);
        }

        public GraphicsState State { get { return this.state; } }

        // Why the last Apply or Adopt did not do all it was asked to, or null
        public string LastError { get; private set; }

        private void Log(string text) {
            ChangeLog.Append(this.state.LogPath, DateTime.Now, "Graphics apps: " + text);
        }

        // The values of the given apps now (null = none)
        private Dictionary<string, string> Current(IEnumerable<string> apps) {
            Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach(string app in apps)
                current[app] = this.gpu.Get(app);
            return current;
        }

        // What applying a profile (null = Windows default) would do now; changes nothing
        public GraphicsPlan Plan(GraphicsProfile target) {
            lock(this.sync) {
                this.state.Reload();
                return GraphicsPlanner.Plan(target,
                    Current(GraphicsPlanner.Apps(target, this.state.Managed.Keys)), this.state.Managed);
            }
        }

        // Applies a profile (null = Windows default) to the registry; source names the
        // caller in the log ("-Graphics Apply", "Power window"). Restarts nothing.
        public GraphicsResult Apply(GraphicsProfile target, string source) {
            lock(this.sync) {
                this.LastError = null;
                GraphicsResult result = new GraphicsResult();

                // While the saved originals cannot be read, nothing is changed or restored
                if(!this.state.Reload()) {
                    result.Errors.Add("the saved originals cannot be read, so nothing was changed");
                    this.LastError = result.Error;
                    return result;
                }

                GraphicsPlan plan = GraphicsPlanner.Plan(target,
                    Current(GraphicsPlanner.Apps(target, this.state.Managed.Keys)), this.state.Managed);
                result.Plan = plan;
                if(plan.HasChanges)
                    Log(source + ": applying " + plan.TargetName);

                foreach(GraphicsAction a in plan.Actions) {
                    if(a.Kind == GraphicsActionKind.Set) {
                        if(!Set(a, result))
                            break;
                    } else if(a.Kind == GraphicsActionKind.Restore)
                        Restore(a, result);
                    else if(a.Kind == GraphicsActionKind.Unmanage) {
                        this.state.Managed.Remove(a.App);
                        SaveRestored();
                        result.Done.Add(a);
                        Log(a.Describe());
                    }
                }

                this.LastError = result.Error;
                return result;
            }
        }

        // The original (or the new value OmenMon writes) on disk first, then the value;
        // false if the state file could not be saved: nothing more is changed then
        private bool Set(GraphicsAction a, GraphicsResult result) {
            bool had = this.state.Managed.TryGetValue(a.App, out GraphicsManaged before);
            this.state.Managed[a.App] = new GraphicsManaged(had ? before.Original : a.Before, a.After);
            if(!this.state.Save()) {
                Forget(a.App, had, before);
                string error = "could not save the original value, so " + a.App
                    + " and the apps after it were left as they are: " + this.state.LastError;
                result.Errors.Add(error);
                Log(error);
                return false;
            }
            if(a.Writes)
                try {
                    this.gpu.Set(a.App, a.After);
                } catch(Exception e) {
                    // The value is as it was: so is the state file
                    Forget(a.App, had, before);
                    SaveRestored();
                    string error = "could not set the GPU preference of " + a.App + ": " + e.Message;
                    result.Errors.Add(error);
                    Log(error);
                    return true;
                }
            result.Done.Add(a);
            Log(a.Describe());
            return true;
        }

        private void Forget(string app, bool had, GraphicsManaged before) {
            if(had)
                this.state.Managed[app] = before;
            else
                this.state.Managed.Remove(app);
        }

        // The value back first, then the state file: if that cannot be saved, it still
        // lists the app, and the next apply finds it no longer as OmenMon set it and
        // drops it. A value that cannot be written stays managed, for the next apply.
        private void Restore(GraphicsAction a, GraphicsResult result) {
            if(a.Writes)
                try {
                    if(a.After == null)
                        this.gpu.Remove(a.App);
                    else
                        this.gpu.Set(a.App, a.After);
                } catch(Exception e) {
                    string error = "could not restore the GPU preference of " + a.App + ": " + e.Message;
                    result.Errors.Add(error);
                    Log(error);
                    return;
                }
            this.state.Managed.Remove(a.App);
            SaveRestored();
            result.Done.Add(a);
            Log(a.Describe());
        }

        private void SaveRestored() {
            if(!this.state.Save())
                Log("could not update the state file: " + this.state.LastError);
        }

        // Makes a profile of the GPU preferences set now (see GraphicsAdoption) and
        // manages its apps, with the originals given (by app; null: the values now).
        // An app OmenMon manages already keeps the original it has. Changes nothing in
        // the registry. Returns the profile (the caller adds it to the configuration),
        // or null if the originals could not be saved (LastError says why).
        public GraphicsProfile Adopt(string name, IDictionary<string, string> originals) {
            lock(this.sync) {
                this.LastError = null;
                if(!this.state.Reload()) {
                    this.LastError = "the saved originals cannot be read, so nothing was adopted";
                    return null;
                }
                Dictionary<string, GraphicsManaged> managed = new Dictionary<string, GraphicsManaged>(StringComparer.OrdinalIgnoreCase);
                GraphicsProfile profile = GraphicsAdoption.Build(name, Current(this.gpu.Apps()), originals, managed);
                List<string> added = managed.Keys.Where(app => !this.state.Managed.ContainsKey(app)).ToList();
                foreach(string app in added)
                    this.state.Managed[app] = managed[app];
                if(added.Count > 0 && !this.state.Save()) {
                    foreach(string app in added)
                        this.state.Managed.Remove(app);
                    this.LastError = "could not save the originals, so nothing was adopted: " + this.state.LastError;
                    Log(this.LastError);
                    return null;
                }
                Log("adopted profile " + name + ": " + profile.Apps.Count + " app(s), originals from "
                    + (originals == null ? "their values now" : "the .reg file"));
                foreach(string app in added)
                    Log(app + ": managed, original " + Show(managed[app].Original) + ", now " + Show(managed[app].Written));
                return profile;
            }
        }

        // A value for the log and the command line: its meaning and as it is written
        public static string Show(string value) {
            return value == null ? "none" : GpuValue.Describe(value) + " [" + value + "]";
        }

    }

    // What Apply did
    public sealed class GraphicsResult {

        public GraphicsPlan Plan;
        public readonly List<GraphicsAction> Done = new List<GraphicsAction>();
        public readonly List<string> Errors = new List<string>();

        // Null if everything planned was done
        public string Error { get { return this.Errors.Count > 0 ? string.Join("; ", this.Errors) : null; } }

        // The apps whose GPU changed: they take it when they start again
        public List<string> Restart {
            get { return this.Done.Where(a => a.ChoiceChanges).Select(a => a.App).ToList(); }
        }

    }

    // The apps Graphics apps manages. Text file, one line per app:
    //   gpu <TAB> app <TAB> original value (empty = none) <TAB> value OmenMon wrote
    // Saved before the registry is changed, with the robustness of StateFile.
    public sealed class GraphicsState : StateFile {

        public const string FileName = "OmenMon-Graphics.state";
        public const string LogName = "OmenMon-Graphics.log";

        public readonly Dictionary<string, GraphicsManaged> Managed = new Dictionary<string, GraphicsManaged>(StringComparer.OrdinalIgnoreCase);

        public GraphicsState(string path) : base(path) { }

        protected override string Owner { get { return "Graphics apps"; } }
        protected override string LogFileName { get { return LogName; } }

        public static GraphicsState Load(string path) {
            GraphicsState s = new GraphicsState(path);
            s.LoadFile();
            return s;
        }

        protected override void Parse(string[] lines) {
            this.Managed.Clear();
            foreach(string line in lines) {
                string[] f = line.Split('\t');
                if(f.Length == 4 && f[0] == "gpu" && f[1].Length > 0 && f[3].Length > 0)
                    this.Managed[f[1]] = new GraphicsManaged(f[2].Length > 0 ? f[2] : null, f[3]);
            }
        }

        public override bool IsEmpty { get { return this.Managed.Count == 0; } }

        protected override string Format() {
            StringBuilder sb = new StringBuilder();
            foreach(KeyValuePair<string, GraphicsManaged> m in this.Managed)
                sb.Append("gpu\t").Append(m.Key).Append('\t').Append(m.Value.Original ?? "")
                  .Append('\t').Append(m.Value.Written).Append("\r\n");
            return sb.ToString();
        }

    }

    // Moves the GPU preferences an older Eco+ set to power saving (its "gpu" lines) into
    // the Graphics apps state, as managed apps OmenMon wrote "GpuPreference=1;" for.
    // The graphics state is saved first, then the lines are dropped from the Eco+ state;
    // a line found in both (a crash or a failed save in between) is only dropped, so
    // running it again changes nothing. Called on start, before anything else saves
    // either state.
    public static class GraphicsMigration {

        public const string EcoPlusWritten = "GpuPreference=1;";

        // A description of what was moved, or null if there was nothing (or it could not
        // be moved: error says why, and the Eco+ state keeps the lines)
        public static string FromEcoPlus(EcoPlusState eco, GraphicsState graphics, out string error) {
            error = null;
            if(eco.SavedGpu.Count == 0)
                return null;
            if(!graphics.Reload()) {
                error = "GPU preferences saved by Eco+ not moved: the Graphics apps state cannot be read";
                ChangeLog.Append(graphics.LogPath, DateTime.Now, "Graphics apps: " + error);
                return null;
            }

            List<string> added = new List<string>();
            foreach(KeyValuePair<string, string> g in eco.SavedGpu)
                if(!graphics.Managed.ContainsKey(g.Key)) {
                    graphics.Managed[g.Key] = new GraphicsManaged(g.Value, EcoPlusWritten);
                    added.Add(g.Key);
                }
            if(added.Count > 0 && !graphics.Save()) {
                foreach(string app in added)
                    graphics.Managed.Remove(app);
                error = "GPU preferences saved by Eco+ not moved, the Graphics apps state could not be saved: " + graphics.LastError;
                ChangeLog.Append(graphics.LogPath, DateTime.Now, "Graphics apps: " + error);
                return null;
            }

            int already = eco.SavedGpu.Count - added.Count;
            eco.SavedGpu.Clear();
            string done = added.Count + " GPU preference(s) moved from Eco+ to Graphics apps"
                + (already > 0 ? ", " + already + " moved before" : "");
            if(!eco.Save())
                done += " (the Eco+ state file still lists them, dropped on the next start: " + eco.LastError + ")";
            ChangeLog.Append(graphics.LogPath, DateTime.Now, "Graphics apps: " + done);
            foreach(string app in added)
                ChangeLog.Append(graphics.LogPath, DateTime.Now, "Graphics apps: " + app + ": managed, original "
                    + GraphicsApps.Show(graphics.Managed[app].Original) + ", Eco+ had set power saving");
            return done;
        }

    }

}
