  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OmenMon.Library {

    // Graphics apps, the window's side (GuiFormGraphics, its restart dialog and the
    // Power window's "Add to a profile"): every decision the forms make, kept here so
    // it can be tested with fakes. The forms only show what these return and pass the
    // user's clicks on.
    //
    //  * Editing profiles: each edit is saved at once; while the configuration file did
    //    not load, nothing is edited or saved. A save that fails puts everything back.
    //  * Applying: the plan is shown first, the profile is marked active only once all
    //    of it was applied.
    //  * Restarting: only apps that can be restarted can be checked; an app still running
    //    after the polite close is ended only if the user says so, app by app; signing
    //    out or restarting Windows takes a second, explicit confirmation.
    //  * After a start: the registry is compared with the active profile, never changed.

#region Labels
    // The GPU choices as the window names them
    public static class GraphicsChoiceLabel {

        public const string PowerSaving = "Power saving";
        public const string HighPerformance = "High performance";
        public const string Auto = "Let Windows decide";

        // In the order the dropdown lists them
        public static readonly string[] All = { PowerSaving, HighPerformance, Auto };

        public static string Of(GpuChoice choice) {
            switch(choice) {
                case GpuChoice.PowerSaving: return PowerSaving;
                case GpuChoice.HighPerformance: return HighPerformance;
                default: return Auto;
            }
        }

        // Null if the label is not one of All
        public static GpuChoice? Parse(string label) {
            switch(label) {
                case PowerSaving: return GpuChoice.PowerSaving;
                case HighPerformance: return GpuChoice.HighPerformance;
                case Auto: return GpuChoice.Auto;
                default: return null;
            }
        }

    }
#endregion

#region Lists
    // One entry of the profile list: Windows default first, then the profiles by name
    public sealed class GraphicsProfileItem {
        public string Name;                 // the profile's name; Windows default: WindowsDefaultName
        public bool IsWindowsDefault;
        public bool Active;
        public int Apps;                    // -1 for Windows default

        public override string ToString() {
            string text = this.IsWindowsDefault ? this.Name + " (built in)" : this.Name + " (" + this.Apps + ")";
            return this.Active ? "● " + text + "  active" : "   " + text;
        }
    }

    // One row of the app list
    public sealed class GraphicsAppRow {
        public string Key;          // exe path or AUMID
        public string Name;         // shown in the list
        public string Choice;       // a GraphicsChoiceLabel; Windows default: what it gets back
        public string Now;          // the registry value now, as GpuValue.Describe has it
        public bool Editable;       // the choice can be changed, the app removed
    }

    // A running app with a window, for "Add running app"
    public sealed class RunningApp {
        public int Pid;
        public string Key;          // AUMID or exe path, as the GPU preference store names it
        public string Path;         // the executable, if readable
        public string Name;
        public bool HoldsGpu;       // holds a context on the discrete GPU (last status reading)
        public bool InProfile;      // the selected profile has it already

        public override string ToString() {
            return this.Name + (this.HoldsGpu ? "  [keeps the discrete GPU awake]" : "")
                + (this.InProfile ? "  [in the profile already]" : "") + "   " + this.Key;
        }
    }

    public static class GraphicsLists {

        public static List<GraphicsProfileItem> Profiles(IDictionary<string, GraphicsProfile> profiles, string active) {
            List<GraphicsProfileItem> items = new List<GraphicsProfileItem> {
                new GraphicsProfileItem { Name = GraphicsProfile.WindowsDefaultName, IsWindowsDefault = true, Apps = -1,
                    Active = string.IsNullOrEmpty(active) } };
            foreach(KeyValuePair<string, GraphicsProfile> p in profiles.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                items.Add(new GraphicsProfileItem { Name = p.Value.Name, Apps = p.Value.Apps.Count,
                    Active = string.Equals(p.Key, active, StringComparison.OrdinalIgnoreCase) });
            return items;
        }

        // A profile's apps, each with its value now (current: null = none)
        public static List<GraphicsAppRow> ForProfile(GraphicsProfile profile, Func<string, string> current) {
            List<GraphicsAppRow> rows = new List<GraphicsAppRow>();
            if(profile != null)
                foreach(GraphicsProfileApp app in profile.Apps)
                    rows.Add(new GraphicsAppRow { Key = app.Key, Name = Name(app.Key), Choice = GraphicsChoiceLabel.Of(app.Choice),
                        Now = GpuValue.Describe(Read(current, app.Key)), Editable = true });
            return rows;
        }

        // Windows default: the apps OmenMon manages and what each gets back
        public static List<GraphicsAppRow> ForWindowsDefault(IDictionary<string, GraphicsManaged> managed, Func<string, string> current) {
            List<GraphicsAppRow> rows = new List<GraphicsAppRow>();
            if(managed != null)
                foreach(KeyValuePair<string, GraphicsManaged> m in managed.OrderBy(m => Name(m.Key), StringComparer.OrdinalIgnoreCase))
                    rows.Add(new GraphicsAppRow { Key = m.Key, Name = Name(m.Key),
                        Choice = "back to " + (m.Value.Original == null ? "no preference" : GpuValue.Describe(m.Value.Original)),
                        Now = GpuValue.Describe(Read(current, m.Key)), Editable = false });
            return rows;
        }

        private static string Read(Func<string, string> current, string key) {
            try {
                return current?.Invoke(key);
            } catch {
                return null;
            }
        }

        // A short name for an app key: the file name of a path; of an AUMID
        // (PackageFamilyName!AppId) the package name, and the app id unless it is "App"
        public static string Name(string key) {
            if(string.IsNullOrEmpty(key))
                return "?";
            if(key.IndexOf('\\') < 0 && LaunchPolicy.IsValidAumid(key)) {
                int bang = key.IndexOf('!');
                string family = key.Substring(0, bang), id = key.Substring(bang + 1);
                int underscore = family.LastIndexOf('_');
                string package = underscore > 0 ? family.Substring(0, underscore) : family;
                return string.Equals(id, "App", StringComparison.OrdinalIgnoreCase) ? package : package + " (" + id + ")";
            }
            int i = key.LastIndexOfAny(new[] { '\\', '/' });
            return i >= 0 && i < key.Length - 1 ? key.Substring(i + 1) : key;
        }

        // The running apps worth offering: one per app key, not OmenMon itself, not a
        // Windows component (isSystem: by executable path), by name
        public static List<RunningApp> Running(IEnumerable<RunningApp> found, int ownPid, Func<string, bool> isSystem,
            ICollection<string> holders, GraphicsProfile profile) {
            Dictionary<string, RunningApp> byKey = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
            foreach(RunningApp a in found) {
                if(a == null || a.Pid == ownPid || !GraphicsProfile.IsValidApp(a.Key) || byKey.ContainsKey(a.Key))
                    continue;
                if(isSystem != null && isSystem(a.Path))
                    continue;
                byKey[a.Key] = new RunningApp {
                    Pid = a.Pid, Key = a.Key, Path = a.Path, Name = string.IsNullOrEmpty(a.Name) ? Name(a.Key) : a.Name,
                    HoldsGpu = holders != null && holders.Any(h => string.Equals(h, a.Key, StringComparison.OrdinalIgnoreCase)),
                    InProfile = profile != null && profile.Contains(a.Key) };
            }
            return byKey.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

    }
#endregion

#region Editing profiles
    // Edits the graphics profiles and saves each edit at once. Every method returns null
    // if it was done, or what to tell the user; an edit that is refused or whose save
    // fails leaves the profiles and the active one exactly as they were.
    public sealed class GraphicsProfileEditor {

        private readonly Func<IDictionary<string, GraphicsProfile>> profiles;
        private readonly Func<string> getActive;
        private readonly Action<string> setActive;
        private readonly Func<bool> loadFailed;
        private readonly Action save;

        // save throws if the file could not be written (Config.SaveCore)
        public GraphicsProfileEditor(Func<IDictionary<string, GraphicsProfile>> profiles, Func<string> getActive,
            Action<string> setActive, Func<bool> loadFailed, Action save) {
            this.profiles = profiles;
            this.getActive = getActive;
            this.setActive = setActive;
            this.loadFailed = loadFailed;
            this.save = save;
        }

        public const string LoadFailedText = "The configuration file (OmenMon.xml) did not load, so profiles are not changed or saved"
            + " here. Fix the file and start OmenMon again.";

        public bool CanEdit { get { return !this.loadFailed(); } }

        public IDictionary<string, GraphicsProfile> Profiles { get { return this.profiles(); } }

        public string Active { get { return this.getActive() ?? ""; } }

        public GraphicsProfile Find(string name) {
            return GraphicsProfile.Find(Profiles, name, out GraphicsProfile p) ? p : null;
        }

        // Why a name cannot be taken (except: the profile being renamed), or null
        public string CheckName(string name, string except = null) {
            if(!GraphicsProfile.IsValidName(name))
                return "\"" + name + "\" cannot be a profile name: it must not be empty, start or end with a space,"
                    + " start with '-', or be \"" + GraphicsProfile.DefaultKeyword + "\" or \"" + GraphicsProfile.WindowsDefaultName + "\"";
            GraphicsProfile existing = Find(name);
            if(existing != null && !string.Equals(existing.Name, except, StringComparison.OrdinalIgnoreCase))
                return "A profile named " + existing.Name + " exists already";
            return null;
        }

        // A name not taken yet: "New profile", "New profile 2", ...
        public string FreeName(string stem) {
            if(Find(stem) == null && GraphicsProfile.IsValidName(stem))
                return stem;
            for(int n = 2; ; n++)
                if(Find(stem + " " + n) == null)
                    return stem + " " + n;
        }

        public string New(string name) {
            return Edit(() => CheckName(name) ?? Put(new GraphicsProfile(name)));
        }

        public string Duplicate(string source, string name) {
            return Edit(() => {
                GraphicsProfile from = Find(source);
                if(from == null)
                    return "There is no profile named " + source;
                return CheckName(name) ?? Put(from.Copy(name));
            });
        }

        // The active profile keeps being the active one under its new name
        public string Rename(string oldName, string newName) {
            return Edit(() => {
                GraphicsProfile from = Find(oldName);
                if(from == null)
                    return "There is no profile named " + oldName;
                string why = CheckName(newName, from.Name);
                if(why != null)
                    return why;
                if(from.Name == newName)
                    return null;
                bool active = string.Equals(Active, from.Name, StringComparison.OrdinalIgnoreCase);
                Profiles.Remove(from.Name);
                Put(from.Copy(newName));
                if(active)
                    this.setActive(newName);
                return null;
            });
        }

        // The active profile is what the registry holds: it is not deleted
        public string Delete(string name) {
            return Edit(() => {
                GraphicsProfile p = Find(name);
                if(p == null)
                    return "There is no profile named " + name;
                if(string.Equals(Active, p.Name, StringComparison.OrdinalIgnoreCase))
                    return p.Name + " is the active profile (the GPU preferences are set as it has them): apply another"
                        + " profile or " + GraphicsProfile.WindowsDefaultName + " first, then delete it";
                Profiles.Remove(p.Name);
                return null;
            });
        }

        // Adds apps (those in the profile already keep their choice); added: how many were new
        public string AddApps(string profile, IEnumerable<string> apps, GpuChoice choice, out int added) {
            int count = 0;
            string result = Edit(() => {
                GraphicsProfile p = Find(profile);
                if(p == null)
                    return "There is no profile named " + profile;
                foreach(string app in apps)
                    if(GraphicsProfile.IsValidApp(app) && !p.Contains(app)) {
                        p.Set(app, choice);
                        count++;
                    }
                return null;
            });
            added = result == null ? count : 0;
            return result;
        }

        public string SetChoice(string profile, string app, GpuChoice choice) {
            return Edit(() => {
                GraphicsProfile p = Find(profile);
                if(p == null)
                    return "There is no profile named " + profile;
                if(!p.Contains(app))
                    return app + " is not in " + p.Name;
                if(p.Get(app) != choice)
                    p.Set(app, choice);
                return null;
            });
        }

        public string RemoveApp(string profile, string app) {
            return Edit(() => {
                GraphicsProfile p = Find(profile);
                if(p == null)
                    return "There is no profile named " + profile;
                return p.Remove(app) ? null : app + " is not in " + p.Name;
            });
        }

        // After applying a profile (null: Windows default): it becomes the active one only
        // if all of it was applied. Returns null, or a note on why it was not marked.
        public string AfterApply(GraphicsProfile target, GraphicsResult result) {
            if(result == null)
                return "nothing was applied";
            if(result.Error != null)
                return "not marked as the active profile, as not all of it was applied";
            string name = target != null ? target.Name : "";
            if(Active == name)
                return null;
            if(!CanEdit)
                return "applied, but not saved as the active profile: OmenMon.xml did not load";
            return Edit(() => {
                this.setActive(name);
                return null;
            });
        }

        private string Put(GraphicsProfile p) {
            Profiles[p.Name] = p;
            return null;
        }

        // Runs an edit and saves it; the profiles and the active one go back to what they
        // were if it is refused or cannot be saved
        private string Edit(Func<string> edit) {
            if(!CanEdit)
                return LoadFailedText;
            IDictionary<string, GraphicsProfile> all = Profiles;
            List<GraphicsProfile> before = all.Values.Select(p => p.Copy(p.Name)).ToList();
            string activeBefore = this.getActive();
            string why;
            try {
                why = edit();
                if(why == null)
                    this.save();
            } catch(Exception e) {
                why = "Could not save the configuration file: " + e.Message;
            }
            if(why != null) {
                all.Clear();
                foreach(GraphicsProfile p in before)
                    all[p.Name] = p;
                this.setActive(activeBefore);
            }
            return why;
        }

    }
#endregion

#region Texts
    // What the window and its dialogs say
    public static class GraphicsUiText {

        public const string NotRunningNote = "Apps that are not running use the new GPU when they start.";

        // What applying a plan changes, for the confirmation
        public static string PlanSummary(GraphicsPlan plan) {
            StringBuilder sb = new StringBuilder();
            List<GraphicsAction> writes = plan.Actions.Where(a => a.Writes).ToList();
            List<GraphicsAction> manage = plan.Actions.Where(a => !a.Writes && (a.Kind == GraphicsActionKind.Set || a.Kind == GraphicsActionKind.Restore)).ToList();
            List<GraphicsAction> outside = plan.Actions.Where(a => a.Kind == GraphicsActionKind.Unmanage).ToList();
            int same = plan.Actions.Count(a => a.Kind == GraphicsActionKind.NoChange);

            sb.Append("Apply ").Append(plan.TargetName).Append(plan.Target == null ? " (every app OmenMon manages gets its original back)" : "")
              .Append("\r\n\r\n");
            if(!plan.HasChanges)
                sb.Append("Nothing to change: the GPU preferences are as ").Append(plan.TargetName).Append(" has them.\r\n");
            if(writes.Count > 0) {
                sb.Append("GPU preferences written (HKCU\\").Append(RegistryGpuPreferences.KeyPath).Append("):\r\n");
                foreach(GraphicsAction a in writes)
                    sb.Append("  ").Append(a.Describe()).Append("\r\n");
            }
            if(manage.Count > 0) {
                sb.Append("No change in the registry, only in what OmenMon keeps:\r\n");
                foreach(GraphicsAction a in manage)
                    sb.Append("  ").Append(a.Describe()).Append("\r\n");
            }
            if(outside.Count > 0) {
                sb.Append("Changed in Windows Settings: left as it is (no longer managed by OmenMon):\r\n");
                foreach(GraphicsAction a in outside)
                    sb.Append("  ").Append(a.App).Append(": now ").Append(GpuValue.Describe(a.Before))
                      .Append(", OmenMon had set ").Append(GpuValue.Describe(a.Written)).Append("\r\n");
            }
            if(same > 0)
                sb.Append("Already as the profile has it: ").Append(same).Append(" app(s)\r\n");
            int restart = plan.Actions.Count(a => a.ChoiceChanges);
            if(restart > 0)
                sb.Append("\r\n").Append(restart).Append(" app(s) get another GPU. A running app takes it only when it starts again:"
                    + " OmenMon lists the running ones next and restarts only those you check.\r\n");
            return sb.ToString();
        }

        // The line for the status area and the log after an apply
        public static string Applied(GraphicsPlan plan, GraphicsResult result, string activeNote) {
            int written = result.Done.Count(a => a.Writes);
            int outside = result.Done.Count(a => a.Kind == GraphicsActionKind.Unmanage);
            string text = "Applied " + plan.TargetName + ": " + written + " value(s) written"
                + (outside > 0 ? ", " + outside + " changed in Windows Settings left as they are" : "");
            if(result.Error != null)
                text += ". Not all of it: " + result.Error;
            if(activeNote != null)
                text += " (" + activeNote + ")";
            return text;
        }

        public static string DeleteConfirmation(GraphicsProfile p) {
            return "Delete the profile " + p.Name + " (" + p.Apps.Count + " app(s))?\r\n\r\n"
                + "Only the profile is deleted: no GPU preference changes.";
        }

        // The question for an app still running after the polite close
        public static string DecisionQuestion(RestartResult r) {
            string name = r.Entry?.DisplayName ?? GraphicsLists.Name(r.AppKey);
            string detail = r.Detail ?? "still running";
            string text = detail.StartsWith("still running", StringComparison.Ordinal) ? name + " is " + detail : name + ": " + detail;
            if(r.StillRunning.Count > 0)
                text += " (" + r.StillRunning.Count + " process(es): " + string.Join(", ", r.StillRunning.Take(5).Select(p => p.ToString()))
                    + (r.StillRunning.Count > 5 ? ", ..." : "") + ")";
            return text + ".\r\n\r\nEnd it: it is ended at once, unsaved work in it is lost, and it is started again.\r\n"
                + "Leave it running: nothing happens to it; it uses the new GPU at its next start.";
        }

        // The second confirmation before the session ends
        public static string SessionConfirmation(SessionEnd how, string profile) {
            string what = how == SessionEnd.SignOut ? "Sign out of Windows now?" : "Restart Windows now?";
            StringBuilder sb = new StringBuilder(what).Append("\r\n\r\n");
            sb.Append("Windows closes every app that is open (an app with unsaved work can ask you first),"
                + " and OmenMon exits too: fan control goes back to the BIOS"
                + " until OmenMon runs again");
            if(how == SessionEnd.SignOut)
                sb.Append(" after you sign in.\r\n");
            else
                sb.Append(" after the restart. The computer is not usable while it restarts.\r\n");
            sb.Append("\r\nThe GPU preferences");
            if(!string.IsNullOrEmpty(profile))
                sb.Append(" of ").Append(profile);
            sb.Append(" are written already: the apps use them when they start again, Explorer too."
                + " When OmenMon starts, it checks that the registry is as the active profile has it.");
            return sb.ToString();
        }

        public static string GpuState(int state) {
            switch(state) {
                case 1: return "on (D0)";
                case 2: return "low power (D1)";
                case 3: return "low power (D2)";
                case 4: return "off (D3)";
                default: return "unknown";
            }
        }

        // The discrete GPU's state and the apps holding it. holdersRead: NVML was read
        // (only while the GPU is on); Windows components (isSystem, by path) are counted
        public static string GpuStatus(int state, bool holdersRead, IEnumerable<GpuHolder> holders, Func<string, bool> isSystem) {
            string text = "Discrete GPU: " + GpuState(state);
            if(state != 1)
                return text + (state == 4 ? ": no app keeps it awake" : "");
            if(!holdersRead)
                return text + "; which apps hold it could not be read";
            List<GpuHolder> all = (holders ?? Enumerable.Empty<GpuHolder>()).ToList();
            if(all.Count == 0)
                return text + "; no app holds it (it can power off when idle)";
            List<GpuHolder> apps = all.Where(h => isSystem == null || !isSystem(h.Path)).ToList();
            int system = all.Count - apps.Count;
            text += ", held by: " + (apps.Count > 0 ? string.Join(", ", apps.Select(h => (h.Name ?? "?") + " (" + h.Pid + ")")) : "");
            if(system > 0)
                text += (apps.Count > 0 ? " and " : "") + system + " Windows component(s)";
            return text;
        }

    }
#endregion

#region Restart dialog
    // One row of the restart dialog
    public sealed class RestartRow {
        public RestartEntry Entry;      // null: the running apps could not be listed
        public string AppKey;
        public string Name;
        public int Processes;
        public bool Checkable;
        public bool Checked;
        public string Note;
    }

    public static class GraphicsRestartRows {

        public const string CannotList = "the running apps could not be listed: restart it yourself if it runs";

        // One row per running app (entries; null: they could not be listed, then one
        // row per app key, none of them checkable). Only a restartable app can be
        // checked, and it is checked to begin with.
        public static List<RestartRow> Build(List<RestartEntry> entries, IEnumerable<string> appKeys) {
            List<RestartRow> rows = new List<RestartRow>();
            if(entries == null) {
                foreach(string key in appKeys ?? Enumerable.Empty<string>())
                    rows.Add(new RestartRow { AppKey = key, Name = GraphicsLists.Name(key), Note = CannotList });
                return rows;
            }
            foreach(RestartEntry e in entries) {
                List<string> notes = new List<string>();
                if(!e.Restartable)
                    notes.Add(RestartText.Reason(e.Block));
                else {
                    if(e.ArgumentsUnknown)
                        notes.Add("arguments unknown: started again without them");
                    if(!e.HasVisibleWindow)
                        notes.Add("no window: it can only be ended, you are asked first");
                    if(e.Packaged)
                        notes.Add("packaged app");
                }
                rows.Add(new RestartRow {
                    Entry = e, AppKey = e.AppKey, Name = e.DisplayName, Processes = e.Processes.Count,
                    Checkable = e.Restartable, Checked = e.Restartable, Note = string.Join("; ", notes) });
            }
            return rows;
        }

        // The entries to restart: checked and checkable, nothing else
        public static List<RestartEntry> Confirmed(IEnumerable<RestartRow> rows) {
            return rows.Where(r => r.Checked && r.Checkable && r.Entry != null && r.Entry.Restartable).Select(r => r.Entry).ToList();
        }

        // Whether to show the dialog after an apply: some app whose GPU changed runs (or
        // that cannot be known)
        public static bool Show(List<RestartEntry> entries, ICollection<string> appKeys) {
            return appKeys != null && appKeys.Count > 0 && (entries == null || entries.Count > 0);
        }

    }

    // Restarts the confirmed apps one after the other (on a worker thread). For every
    // app still running after the polite close, askEnd asks the user (true: end it);
    // nothing is ended without that answer.
    public static class GraphicsRestartRun {

        public static List<RestartResult> Run(IList<RestartEntry> confirmed,
            Func<RestartEntry, RestartResult> restart, Func<RestartResult, bool, RestartResult> decide,
            Func<RestartResult, bool> askEnd, Action<int, int, RestartEntry> progress, Action<string> log) {
            List<RestartResult> results = new List<RestartResult>();
            if(confirmed == null)
                return results;
            log?.Invoke("restarting " + confirmed.Count + " app(s): " + string.Join(", ", confirmed.Select(e => e.DisplayName)));
            for(int i = 0; i < confirmed.Count; i++) {
                RestartEntry e = confirmed[i];
                progress?.Invoke(i, confirmed.Count, e);
                RestartResult r;
                try {
                    r = restart(e);
                } catch(Exception ex) {
                    r = new RestartResult { AppKey = e.AppKey, Entry = e, Outcome = RestartOutcome.LaunchFailed, Detail = "failed: " + ex.Message };
                    log?.Invoke(RestartText.Describe(r));
                }
                if(r != null && r.Outcome == RestartOutcome.NeedsDecision) {
                    bool end = false;
                    try {
                        end = askEnd(r);
                    } catch { }
                    log?.Invoke((r.Entry?.DisplayName ?? r.AppKey) + ": the user chose to " + (end ? "end it" : "leave it running"));
                    try {
                        r = decide(r, end) ?? r;
                    } catch(Exception ex) {
                        r = new RestartResult { AppKey = e.AppKey, Entry = e, Outcome = RestartOutcome.EndFailed, Detail = "failed: " + ex.Message,
                            StillRunning = r.StillRunning };
                        log?.Invoke(RestartText.Describe(r));
                    }
                }
                results.Add(r ?? new RestartResult { AppKey = e.AppKey, Entry = e, Outcome = RestartOutcome.LaunchFailed, Detail = "no result" });
            }
            progress?.Invoke(confirmed.Count, confirmed.Count, null);
            return results;
        }

    }
#endregion

#region Ending the session
    public enum SessionEnd {
        SignOut,
        RestartWindows
    }

    // Signing out and restarting Windows (ExitWindowsEx); a fake in the tests
    public interface ISessionControl {
        // False with the reason if Windows refused
        bool End(SessionEnd how, out string error);
    }

    public static class GraphicsSession {

        // Ends the session only if the user confirms the text that names what happens
        // (confirm: true = go ahead). True if Windows was asked to; error says why not
        // (null if the user cancelled).
        public static bool Run(SessionEnd how, string profile, Func<string, bool> confirm, ISessionControl control,
            Action<string> log, out string error) {
            error = null;
            string what = how == SessionEnd.SignOut ? "sign out" : "restart Windows";
            if(confirm == null || control == null || !confirm(GraphicsUiText.SessionConfirmation(how, profile))) {
                log?.Invoke(what + ": cancelled");
                return false;
            }
            log?.Invoke(what + ": confirmed");
            bool done;
            try {
                done = control.End(how, out error);
            } catch(Exception e) {
                done = false;
                error = e.Message;
            }
            if(!done) {
                error = error ?? "Windows refused";
                log?.Invoke(what + ": failed: " + error);
            }
            return done;
        }

    }
#endregion

#region Check after a start
    // How the registry differs from the active profile
    public sealed class GraphicsDrift {
        public string Profile;
        public int Apps;
        public string Text;
    }

    // After OmenMon starts (a reboot, a sign-in), whether the GPU preferences are still
    // as the active profile has them. Nothing is applied: the difference is logged and
    // shown, and applying it again is up to the user.
    public static class GraphicsStartupCheck {

        // The apps whose registry value is not as the profile has it (or that were
        // changed in Windows Settings)
        public static int Differences(GraphicsPlan plan) {
            return plan == null ? 0 : plan.Actions.Count(a => a.Writes || a.Kind == GraphicsActionKind.Unmanage);
        }

        // Null if no profile is active or there is no such profile; else the drift, Apps
        // 0 if the registry matches (plan: GraphicsApps.Plan, which changes nothing)
        public static GraphicsDrift Check(IDictionary<string, GraphicsProfile> profiles, string active, Func<GraphicsProfile, GraphicsPlan> plan) {
            if(string.IsNullOrEmpty(active) || !GraphicsProfile.Find(profiles, active, out GraphicsProfile profile) || profile == null)
                return null;
            int n = Differences(plan(profile));
            return new GraphicsDrift { Profile = profile.Name, Apps = n,
                Text = n > 0 ? "registry differs from the active profile " + profile.Name + ": " + n + " app(s)"
                    : "registry matches the active profile " + profile.Name };
        }

    }
#endregion

#region Power window
    // Which profile the Power window adds apps to as power saving
    public static class GraphicsPowerTarget {

        public const string NewPowerSaving = GraphicsProfile.PowerSavingName + " (new)";

        // The choices if there is more than one profile (null: no choice to make, the
        // active profile or "Power saving" is taken); selected: the active one, else
        // "Power saving"
        public static List<string> Choices(IDictionary<string, GraphicsProfile> profiles, string active, out int selected) {
            selected = 0;
            if(profiles == null || profiles.Count <= 1)
                return null;
            List<string> choices = profiles.Values.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if(!GraphicsProfile.Find(profiles, GraphicsProfile.PowerSavingName, out GraphicsProfile _))
                choices.Add(NewPowerSaving);
            int a = choices.FindIndex(n => string.Equals(n, active, StringComparison.OrdinalIgnoreCase));
            if(a < 0)
                a = choices.FindIndex(n => string.Equals(n, GraphicsProfile.PowerSavingName, StringComparison.OrdinalIgnoreCase)
                    || n == NewPowerSaving);
            selected = Math.Max(0, a);
            return choices;
        }

        // What GraphicsProfile.AddApps takes as the active profile for a choice ("" makes
        // or takes "Power saving" with the apps OmenMon manages now)
        public static string AddTarget(string choice) {
            return choice == NewPowerSaving ? "" : choice;
        }

        // Whether adding to this choice applies it now. A profile that is not the active
        // one only gets the apps: applying it here, without the preview Graphics apps
        // shows, would change every app in it and give the active profile's other apps
        // their originals back. With none active, or "Power saving (new)" (made from the
        // apps OmenMon manages now), only the apps added change, so it is applied.
        public static bool AppliesNow(string choice, string active) {
            return choice == NewPowerSaving || string.IsNullOrEmpty(active)
                || string.Equals(choice, active, StringComparison.OrdinalIgnoreCase);
        }

        // A note under the choice when it is not the active profile
        public static string Note(string choice, string active) {
            string name = choice == NewPowerSaving ? GraphicsProfile.PowerSavingName : choice;
            if(string.Equals(name, active, StringComparison.OrdinalIgnoreCase))
                return "The active profile: only the apps added change.";
            if(choice == NewPowerSaving)
                return "Made from the apps OmenMon manages now, so only the apps added change.";
            if(string.IsNullOrEmpty(active))
                return name + " is not active: all of it is applied.";
            return name + " is not the active profile: the apps are only added to it, and take effect when it is applied in Graphics apps.";
        }

    }
#endregion

}
