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

    // Graphics apps: which GPU each app uses, as set in Windows Settings > Display >
    // Graphics (HKCU\Software\Microsoft\DirectX\UserGpuPreferences: one string value per
    // app, named by its full executable path or a packaged app's AUMID, holding
    // "Key=Value;" pairs such as "GpuPreference=2;"), applied as named profiles.
    //
    // This file is the pure part, with no I/O: the value strings, the profiles, the
    // planner that decides what applying a profile changes, adopting what is set now and
    // reading a .reg export. GraphicsApps.cs keeps the originals and does the writing.
    // Nothing here touches the EC or the BIOS.

    // The GPU an app is to use, as GpuPreference writes it
    public enum GpuChoice {
        Auto = 0,               // let Windows decide
        PowerSaving = 1,        // the integrated GPU
        HighPerformance = 2     // the discrete GPU
    }

    // The GpuPreference pair of a preference value. Every other pair is kept as it is
    // and where it is, so settings Windows keeps in the same value (such as
    // SwapEffectUpgradeEnable) survive whatever OmenMon writes.
    public static class GpuValue {

        public const string Key = "GpuPreference";

        // The pairs of a value as written, without the ';' that ends each
        private static List<string> Pairs(string value) {
            List<string> pairs = new List<string>();
            if(string.IsNullOrEmpty(value))
                return pairs;
            pairs.AddRange(value.Split(';'));
            if(pairs[pairs.Count - 1].Length == 0)
                pairs.RemoveAt(pairs.Count - 1);
            return pairs;
        }

        private static bool IsGpuPair(string pair) {
            int eq = pair.IndexOf('=');
            return eq >= 0 && string.Equals(pair.Substring(0, eq).Trim(), Key, StringComparison.OrdinalIgnoreCase);
        }

        // The GpuPreference token of a value ("2" in "GpuPreference=2;"), null if it has none
        public static string Token(string value) {
            foreach(string pair in Pairs(value))
                if(IsGpuPair(pair))
                    return pair.Substring(pair.IndexOf('=') + 1).Trim();
            return null;
        }

        // The token a choice is written as
        public static string TokenOf(GpuChoice choice) {
            return ((int) choice).ToString(CultureInfo.InvariantCulture);
        }

        // The choice a value stands for (no token: Windows decides); null if the token
        // is not one OmenMon knows
        public static GpuChoice? Choice(string value) {
            switch(Token(value)) {
                case null:
                case "0": return GpuChoice.Auto;
                case "1": return GpuChoice.PowerSaving;
                case "2": return GpuChoice.HighPerformance;
                default: return null;
            }
        }

        // Whether two values give an app the same GPU (no token is the same as 0)
        public static bool SameChoice(string a, string b) {
            return (Token(a) ?? "0") == (Token(b) ?? "0");
        }

        // The value with its GpuPreference token set to the given one (removed if null),
        // every other pair kept in its place. A value that already has that token is
        // returned exactly as it is; otherwise each pair ends with ';', and a value left
        // with no pair at all is "".
        public static string WithToken(string value, string token) {
            if(Token(value) == token)
                return value;
            List<string> result = new List<string>();
            bool placed = false;
            foreach(string pair in Pairs(value)) {
                if(!IsGpuPair(pair))
                    result.Add(pair);
                else if(token != null && !placed) {
                    result.Add(Key + "=" + token);
                    placed = true;
                }
            }
            if(token != null && !placed)
                result.Add(Key + "=" + token);
            return result.Count == 0 ? "" : string.Join(";", result) + ";";
        }

        public static string With(string value, GpuChoice choice) {
            return WithToken(value, TokenOf(choice));
        }

        // Everything but the GpuPreference pair, in a form fit for comparing
        public static string Others(string value) {
            return string.Join(";", Pairs(value).Where(p => !IsGpuPair(p)));
        }

        // Whether a value holds no pair (apart from empty ones)
        public static bool IsEmpty(string value) {
            return Pairs(value).All(p => p.Trim().Length == 0);
        }

        // A readable form of a choice and of a value
        public static string Name(GpuChoice choice) {
            switch(choice) {
                case GpuChoice.PowerSaving: return "power saving";
                case GpuChoice.HighPerformance: return "high performance";
                default: return "let Windows decide";
            }
        }

        public static string Describe(string value) {
            string token = Token(value);
            if(token == null)
                return "default";
            GpuChoice? choice = Choice(value);
            return choice.HasValue ? Name(choice.Value) : Key + "=" + token;
        }

    }

    // One app of a profile: its preference key (full executable path, case-insensitive,
    // or a packaged app's AUMID) and the GPU it is to use
    public sealed class GraphicsProfileApp {
        public readonly string Key;
        public readonly GpuChoice Choice;
        public GraphicsProfileApp(string key, GpuChoice choice) {
            this.Key = key;
            this.Choice = choice;
        }
    }

    // A named set of apps and the GPU each is to use (the wanted state, kept in
    // OmenMon.xml). "Windows default" is built in, not a profile: applying it gives
    // every app OmenMon manages its original back (null wherever a profile is taken).
    public sealed class GraphicsProfile {

        // On the command line, "Default" stands for Windows default
        public const string DefaultKeyword = "Default";
        public const string WindowsDefaultName = "Windows default";

        // The profile the Power window adds to when none is active
        public const string PowerSavingName = "Power saving";

        private readonly List<GraphicsProfileApp> apps = new List<GraphicsProfileApp>();

        public GraphicsProfile(string name) {
            if(!IsValidName(name))
                throw new ArgumentException("Not a valid profile name: " + name);
            this.Name = name;
        }

        public string Name { get; private set; }

        public IList<GraphicsProfileApp> Apps { get { return this.apps.AsReadOnly(); } }

        private int IndexOf(string app) {
            return this.apps.FindIndex(a => string.Equals(a.Key, app, StringComparison.OrdinalIgnoreCase));
        }

        public bool Contains(string app) {
            return IndexOf(app) >= 0;
        }

        public GpuChoice? Get(string app) {
            int i = IndexOf(app);
            return i >= 0 ? this.apps[i].Choice : (GpuChoice?) null;
        }

        // Sets an app's choice; an app already in the profile keeps its place and spelling
        public void Set(string app, GpuChoice choice) {
            if(!IsValidApp(app))
                throw new ArgumentException("Not a valid app key: " + app);
            if(!Enum.IsDefined(typeof(GpuChoice), choice))
                throw new ArgumentException("Not a GPU choice: " + (int) choice);
            int i = IndexOf(app);
            if(i >= 0)
                this.apps[i] = new GraphicsProfileApp(this.apps[i].Key, choice);
            else
                this.apps.Add(new GraphicsProfileApp(app, choice));
        }

        public bool Remove(string app) {
            int i = IndexOf(app);
            if(i < 0)
                return false;
            this.apps.RemoveAt(i);
            return true;
        }

        // The same apps under another name (rename, duplicate)
        public GraphicsProfile Copy(string name) {
            GraphicsProfile copy = new GraphicsProfile(name);
            copy.apps.AddRange(this.apps);
            return copy;
        }

        // A name can be given on the command line and stand in the configuration file:
        // not empty, no surrounding spaces, no control characters, not starting with '-'
        // (that would be the next argument), and not the name of Windows default
        public static bool IsValidName(string name) {
            return !string.IsNullOrEmpty(name) && name.Trim() == name && !name.Any(char.IsControl)
                && !name.StartsWith("-", StringComparison.Ordinal)
                && !string.Equals(name, DefaultKeyword, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, WindowsDefaultName, StringComparison.OrdinalIgnoreCase);
        }

        // An app key fits on a line of the state file: not empty, no control characters
        public static bool IsValidApp(string app) {
            return !string.IsNullOrEmpty(app) && app.Trim().Length > 0 && !app.Any(char.IsControl);
        }

        // Finds a profile by name ("Default" or "Windows default" is null, for Windows
        // default); false if there is no such profile
        public static bool Find(IDictionary<string, GraphicsProfile> profiles, string name, out GraphicsProfile profile) {
            profile = null;
            if(string.Equals(name, DefaultKeyword, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, WindowsDefaultName, StringComparison.OrdinalIgnoreCase))
                return true;
            if(string.IsNullOrEmpty(name))
                return false;
            foreach(KeyValuePair<string, GraphicsProfile> p in profiles)
                if(string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)) {
                    profile = p.Value;
                    return true;
                }
            return false;
        }

        // Adds apps with a choice to the active profile (the Power window's apps keeping
        // the discrete GPU awake); with none active, to "Power saving", made if needed.
        // That one first takes every app OmenMon manages now, at the choice OmenMon set,
        // so that applying it changes nothing but the apps added. Returns the profile,
        // which is in profiles.
        public static GraphicsProfile AddApps(IDictionary<string, GraphicsProfile> profiles, string active,
            IEnumerable<string> apps, GpuChoice choice, IDictionary<string, GraphicsManaged> managed) {
            GraphicsProfile profile;
            if(!string.IsNullOrEmpty(active) && Find(profiles, active, out profile) && profile != null) {
                // The active profile
            } else {
                if(!Find(profiles, PowerSavingName, out profile))
                    profile = new GraphicsProfile(PowerSavingName);
                if(managed != null)
                    foreach(KeyValuePair<string, GraphicsManaged> m in managed) {
                        GpuChoice? set = GpuValue.Choice(m.Value.Written);
                        if(set.HasValue && !profile.Contains(m.Key) && IsValidApp(m.Key))
                            profile.Set(m.Key, set.Value);
                    }
            }
            foreach(string app in apps)
                if(IsValidApp(app))
                    profile.Set(app, choice);
            profiles[profile.Name] = profile;
            return profile;
        }

    }

    // An app OmenMon manages: its value before OmenMon first changed it, and the value
    // OmenMon last wrote (kept in OmenMon-Graphics.state)
    public sealed class GraphicsManaged {
        public readonly string Original;    // null = the app had no value
        public readonly string Written;
        public GraphicsManaged(string original, string written) {
            this.Original = original;
            this.Written = written;
        }
    }

    public enum GraphicsActionKind {
        Set,            // write After (and, if Manage, keep Before as the original)
        Restore,        // put the original back (After, null = remove the value) and stop managing the app
        Unmanage,       // changed outside OmenMon: left as it is, the original forgotten
        NoChange        // already as the profile has it
    }

    // One step of a plan
    public sealed class GraphicsAction {
        public GraphicsActionKind Kind;
        public string App;
        public string Before;           // the value now, null = none
        public string After;            // the value it gets, null = none (Before if nothing is written)
        public bool Manage;             // Set: not managed yet, Before becomes its original
        public string Written;          // Unmanage: the value OmenMon had written

        // The GPU the app gets changes, which takes effect when the app starts again
        public bool ChoiceChanges;

        // Whether the registry value is written (or removed)
        public bool Writes {
            get { return (this.Kind == GraphicsActionKind.Set || this.Kind == GraphicsActionKind.Restore) && this.After != this.Before; }
        }

        public string Describe() {
            switch(this.Kind) {
                case GraphicsActionKind.Set:
                    if(!Writes)
                        return this.App + ": " + GpuValue.Describe(this.Before) + " already, now managed (original kept)";
                    return this.App + ": " + GpuValue.Describe(this.Before) + " -> " + GpuValue.Describe(this.After)
                        + (this.Manage ? " (original kept)" : "");
                case GraphicsActionKind.Restore:
                    if(!Writes)
                        return this.App + ": " + GpuValue.Describe(this.Before) + ", as it was originally; no longer managed";
                    return this.App + ": back to " + (this.After == null ? "no preference (value removed)" : GpuValue.Describe(this.After));
                case GraphicsActionKind.Unmanage:
                    return this.App + ": changed outside OmenMon (now " + GpuValue.Describe(this.Before) + ", OmenMon had set "
                        + GpuValue.Describe(this.Written) + "): left as it is, no longer managed";
                default:
                    return this.App + ": " + GpuValue.Describe(this.Before) + ", unchanged";
            }
        }
    }

    // What applying a profile does, app by app
    public sealed class GraphicsPlan {

        public readonly GraphicsProfile Target;     // null = Windows default
        public readonly List<GraphicsAction> Actions = new List<GraphicsAction>();

        public GraphicsPlan(GraphicsProfile target) {
            this.Target = target;
        }

        public string TargetName {
            get { return this.Target != null ? this.Target.Name : GraphicsProfile.WindowsDefaultName; }
        }

        // Everything but NoChange
        public IEnumerable<GraphicsAction> Changes {
            get { return this.Actions.Where(a => a.Kind != GraphicsActionKind.NoChange); }
        }

        public bool HasChanges { get { return Changes.Any(); } }

        // The apps that take the change when they start again
        public List<string> ChoiceChanged {
            get { return this.Actions.Where(a => a.ChoiceChanges).Select(a => a.App).ToList(); }
        }

    }

    // Decides what applying a profile changes (a pure function of the profile, the
    // current values and what OmenMon manages):
    //  * An app in the profile gets its choice; its value before is kept as the original
    //    the first time OmenMon manages it.
    //  * A managed app not in the profile gets its original back and is no longer managed.
    //  * A managed app whose GPU choice is no longer the one OmenMon wrote was changed
    //    outside OmenMon (Windows Settings): the user's choice wins. It is not
    //    overwritten, no longer managed (its original forgotten), and reported. The
    //    profile keeps the app, so the next apply manages it again from its new value.
    // Only the GpuPreference token counts as a change: another pair Windows keeps in
    // the same value (changed with another setting) is not the user's GPU choice. A
    // restore puts the original back exactly while the other pairs are still the
    // original's, and else only the original's GpuPreference token, keeping them.
    public static class GraphicsPlanner {

        // The apps whose values a plan reads
        public static List<string> Apps(GraphicsProfile target, IEnumerable<string> managed) {
            List<string> apps = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(target != null)
                foreach(GraphicsProfileApp app in target.Apps)
                    if(seen.Add(app.Key))
                        apps.Add(app.Key);
            foreach(string app in managed)
                if(seen.Add(app))
                    apps.Add(app);
            return apps;
        }

        // current: the value of each app now (missing or null = none)
        public static GraphicsPlan Plan(GraphicsProfile target, IDictionary<string, string> current,
            IDictionary<string, GraphicsManaged> managed) {
            GraphicsPlan plan = new GraphicsPlan(target);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if(target != null)
                foreach(GraphicsProfileApp app in target.Apps) {
                    if(!seen.Add(app.Key))
                        continue;
                    string before = Value(current, app.Key);
                    GraphicsManaged m = Managed(managed, app.Key);
                    if(m != null && ChangedOutside(before, m)) {
                        plan.Actions.Add(Unmanage(app.Key, before, m));
                        continue;
                    }
                    string after = GpuValue.With(before, app.Choice);
                    GraphicsAction a = new GraphicsAction {
                        App = app.Key, Before = before, After = after, Manage = m == null,
                        Kind = m == null || after != before ? GraphicsActionKind.Set : GraphicsActionKind.NoChange };
                    a.ChoiceChanges = !GpuValue.SameChoice(before, after);
                    plan.Actions.Add(a);
                }

            foreach(KeyValuePair<string, GraphicsManaged> m in managed) {
                if(!seen.Add(m.Key))
                    continue;
                string before = Value(current, m.Key);
                if(ChangedOutside(before, m.Value)) {
                    plan.Actions.Add(Unmanage(m.Key, before, m.Value));
                    continue;
                }
                string after = RestoreValue(before, m.Value);
                plan.Actions.Add(new GraphicsAction {
                    Kind = GraphicsActionKind.Restore, App = m.Key, Before = before, After = after,
                    ChoiceChanges = !GpuValue.SameChoice(before, after) });
            }
            return plan;
        }

        // Whether the GPU choice of a managed app is no longer the one OmenMon wrote
        public static bool ChangedOutside(string current, GraphicsManaged managed) {
            return GpuValue.Token(current) != GpuValue.Token(managed.Written);
        }

        // The value a managed app gets back (null = none)
        public static string RestoreValue(string current, GraphicsManaged managed) {
            if(GpuValue.Others(current) == GpuValue.Others(managed.Original))
                return managed.Original;
            string after = GpuValue.WithToken(current, GpuValue.Token(managed.Original));
            return managed.Original == null && GpuValue.IsEmpty(after) ? null : after;
        }

        private static GraphicsAction Unmanage(string app, string before, GraphicsManaged m) {
            return new GraphicsAction {
                Kind = GraphicsActionKind.Unmanage, App = app, Before = before, After = before, Written = m.Written };
        }

        private static string Value(IDictionary<string, string> current, string app) {
            if(current == null)
                return null;
            if(current.TryGetValue(app, out string value))
                return value;
            foreach(KeyValuePair<string, string> pair in current)
                if(string.Equals(pair.Key, app, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            return null;
        }

        private static GraphicsManaged Managed(IDictionary<string, GraphicsManaged> managed, string app) {
            if(managed.TryGetValue(app, out GraphicsManaged m))
                return m;
            foreach(KeyValuePair<string, GraphicsManaged> pair in managed)
                if(string.Equals(pair.Key, app, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            return null;
        }

    }

    // Makes a profile of the GPU preferences set now (OmenMon -Graphics Adopt), for
    // entries set by hand before Graphics apps existed
    public static class GraphicsAdoption {

        // Values in the store that are not apps
        public static readonly string[] NotApps = { "DirectXUserGlobalSettings", "GraphicsFeaturesNotificationConfig" };

        // A profile of every app whose value holds a GpuPreference token OmenMon knows,
        // and for each the managed entry: written = its value now, original = its value
        // in originals (missing there: none), or, with no originals at all, its value now
        // (so that Windows default leaves it as it is)
        public static GraphicsProfile Build(string name, IEnumerable<KeyValuePair<string, string>> current,
            IDictionary<string, string> originals, IDictionary<string, GraphicsManaged> managed) {
            GraphicsProfile profile = new GraphicsProfile(name);
            Dictionary<string, string> before = originals == null ? null
                : new Dictionary<string, string>(originals, StringComparer.OrdinalIgnoreCase);
            foreach(KeyValuePair<string, string> app in current) {
                if(app.Value == null || GpuValue.Token(app.Value) == null || !GraphicsProfile.IsValidApp(app.Key)
                    || NotApps.Contains(app.Key, StringComparer.OrdinalIgnoreCase) || profile.Contains(app.Key))
                    continue;
                GpuChoice? choice = GpuValue.Choice(app.Value);
                if(!choice.HasValue)
                    continue;
                profile.Set(app.Key, choice.Value);
                string original = app.Value;
                if(before != null && !before.TryGetValue(app.Key, out original))
                    original = null;
                managed[app.Key] = new GraphicsManaged(original, app.Value);
            }
            return profile;
        }

    }

    // Reads the string values of one key from a .reg file as regedit exports it: version
    // 5 ("Windows Registry Editor Version 5.00", UTF-16) or REGEDIT4, in UTF-16 or UTF-8.
    // Other value types (dword, hex) and the default value are skipped.
    public static class RegFile {

        // The text of a file's bytes, by its byte order mark (UTF-16 without one is
        // recognised by its zero bytes)
        public static string Decode(byte[] bytes) {
            string text;
            if(bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else if(bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            else if(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            else if(bytes.Length >= 2 && bytes[0] != 0 && bytes[1] == 0)
                text = Encoding.Unicode.GetString(bytes);
            else
                text = Encoding.UTF8.GetString(bytes);
            return text.TrimStart('﻿');
        }

        // The string values under the key (keyPath below HKEY_CURRENT_USER, or below a
        // user in HKEY_USERS), by name; null and an error if the file is not a .reg
        // export, has no such key or a line in that key cannot be read
        public static Dictionary<string, string> ReadValues(string text, string keyPath, out string error) {
            error = null;
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = text.Split('\n');
            int first = 0;
            while(first < lines.Length && lines[first].Trim().Length == 0)
                first++;
            string header = first < lines.Length ? lines[first].Trim() : "";
            if(header != "Windows Registry Editor Version 5.00" && header != "REGEDIT4") {
                error = "not a .reg export (no \"Windows Registry Editor Version 5.00\" line at the top)";
                return null;
            }

            bool inKey = false, found = false, continued = false;
            for(int n = first + 1; n < lines.Length; n++) {
                string line = lines[n].TrimEnd('\r');

                // The continuation of a hex value: skipped
                if(continued) {
                    continued = line.EndsWith("\\", StringComparison.Ordinal);
                    continue;
                }
                string trimmed = line.Trim();
                if(trimmed.Length == 0 || trimmed.StartsWith(";", StringComparison.Ordinal))
                    continue;
                if(trimmed.StartsWith("[", StringComparison.Ordinal)) {
                    inKey = trimmed.EndsWith("]", StringComparison.Ordinal) && IsKey(trimmed.Substring(1, trimmed.Length - 2), keyPath);
                    found |= inKey;
                    continue;
                }
                continued = trimmed.EndsWith("\\", StringComparison.Ordinal);
                if(!inKey || trimmed.StartsWith("@", StringComparison.Ordinal))
                    continue;

                int i = 0;
                string name = Quoted(trimmed, ref i);
                if(name == null || i >= trimmed.Length || trimmed[i] != '=') {
                    error = "line " + (n + 1) + " cannot be read";
                    return null;
                }
                i++;
                if(i < trimmed.Length && trimmed[i] == '"') {
                    string value = Quoted(trimmed, ref i);
                    if(value == null || trimmed.Substring(i).Trim().Length > 0) {
                        error = "line " + (n + 1) + " cannot be read";
                        return null;
                    }
                    values[name] = value;
                    continued = false;
                }
                // Anything else (dword:, hex:, hex(2):, - for a deleted value) is not a string
            }
            if(!found) {
                error = "no [HKEY_CURRENT_USER\\" + keyPath + "] key in the file";
                return null;
            }
            return values;
        }

        private const string CurrentUser = "HKEY_CURRENT_USER\\", Users = "HKEY_USERS\\";

        private static bool IsKey(string name, string keyPath) {
            string below = null;
            if(name.StartsWith(CurrentUser, StringComparison.OrdinalIgnoreCase))
                below = name.Substring(CurrentUser.Length);
            else if(name.StartsWith(Users, StringComparison.OrdinalIgnoreCase)) {
                // HKEY_USERS\<the user's SID>\...
                string rest = name.Substring(Users.Length);
                int slash = rest.IndexOf('\\');
                below = slash > 0 ? rest.Substring(slash + 1) : null;
            }
            return below != null && string.Equals(below, keyPath, StringComparison.OrdinalIgnoreCase);
        }

        // A quoted string starting at i (\\ and \" escaped); i is left after its closing
        // quote. Null if there is none.
        private static string Quoted(string line, ref int i) {
            if(i >= line.Length || line[i] != '"')
                return null;
            StringBuilder sb = new StringBuilder();
            for(i++; i < line.Length; i++) {
                char c = line[i];
                if(c == '\\' && i + 1 < line.Length)
                    sb.Append(line[++i]);
                else if(c == '"') {
                    i++;
                    return sb.ToString();
                } else
                    sb.Append(c);
            }
            return null;
        }

    }

}
