  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Collections.Generic;
using System.IO;
using OmenMon.Library;

namespace OmenMon.AppCli {

    // Implements the main operation loop in the application's CLI mode
    // This part covers Graphics apps: the profiles of per-app GPU preferences, the apps
    // OmenMon manages and their originals, applying a profile (the registry part only:
    // no app is restarted from here) and adopting the preferences set now. Reads and
    // writes HKCU UserGpuPreferences, the state file and the configuration; no EC or BIOS.
    public static partial class CliOp {

        private static string GraphicsStatePath {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, GraphicsState.FileName); }
        }

        // Loads the Graphics apps state and moves the GPU preferences an earlier Eco+
        // saved into it. Only while the GUI does not run: it keeps its own copy of both.
        private static GraphicsState GraphicsMigrate(EcoPlusState eco) {
            GraphicsState graphics = GraphicsState.Load(GraphicsStatePath);
            if(graphics.LoadError != null)
                Cli.PrintError("Graphics apps: " + graphics.LoadError);
            string done = GraphicsMigration.FromEcoPlus(eco, graphics, out string error);
            if(done != null)
                Console.WriteLine(done);
            if(error != null)
                Cli.PrintError(error);
            return graphics;
        }

        // Saves the configuration (the profiles, the active one); false if it was not
        // saved, with the reason printed and exit code 1. A file that did not load is
        // not changed from here.
        private static bool GraphicsSaveConfig() {
            if(Config.LoadFailed) {
                Cli.PrintError("Graphics apps: " + Config.FilePath + " did not load, so it was not changed");
                Environment.ExitCode = 1;
                return false;
            }
            try {
                Config.SaveCore();
                return true;
            } catch(Exception e) {
                Cli.PrintError("Graphics apps: could not save " + Config.FilePath + ": " + e.Message);
                Environment.ExitCode = 1;
                return false;
            }
        }

#region Graphics Status
        // Lists the profiles, the active one and the apps OmenMon manages, each with its
        // original, the value OmenMon set and its value now (-Graphics)
        private static void GraphicsShow() {
            GraphicsState state = GraphicsState.Load(GraphicsStatePath);
            if(state.LoadError != null)
                Cli.PrintError("Graphics apps: " + state.LoadError);

            if(Config.GraphicsProfiles.Count == 0)
                Console.WriteLine("No profiles");
            foreach(KeyValuePair<string, GraphicsProfile> p in Config.GraphicsProfiles) {
                bool active = string.Equals(p.Key, Config.GraphicsProfileActive, StringComparison.OrdinalIgnoreCase);
                Console.WriteLine("Profile " + p.Key + (active ? " (active)" : "") + ": " + p.Value.Apps.Count + " app(s)");
                foreach(GraphicsProfileApp app in p.Value.Apps)
                    Console.WriteLine("  " + GpuValue.Name(app.Choice) + ": " + app.Key);
            }
            string activeName = Config.GraphicsProfileActive;
            Console.WriteLine("Active: " + (string.IsNullOrEmpty(activeName) ? "none (" + GraphicsProfile.WindowsDefaultName + ")"
                : activeName + (GraphicsProfile.Find(Config.GraphicsProfiles, activeName, out GraphicsProfile _) ? "" : " (no such profile)")));

            if(state.Managed.Count == 0)
                Console.WriteLine("No app managed: " + GraphicsProfile.WindowsDefaultName + " changes nothing");
            RegistryGpuPreferences gpu = new RegistryGpuPreferences();
            foreach(KeyValuePair<string, GraphicsManaged> m in state.Managed) {
                string now = gpu.Get(m.Key);
                Console.WriteLine("Managed: " + m.Key);
                Console.WriteLine("  original " + GraphicsApps.Show(m.Value.Original) + ", OmenMon set " + GraphicsApps.Show(m.Value.Written)
                    + ", now " + GraphicsApps.Show(now) + (GraphicsPlanner.ChangedOutside(now, m.Value) ? " (changed outside OmenMon)" : ""));
            }

            EcoPlusState eco = EcoPlusState.Load(EcoPlusStatePath);
            if(eco.SavedGpu.Count > 0)
                Console.WriteLine(eco.SavedGpu.Count + " GPU preference(s) saved by an earlier Eco+: they move here"
                    + " on the next start of OmenMon or -Graphics Apply, Restore or Adopt");
        }
#endregion

#region Graphics Apply
        // Applies a profile, or Default (Windows default: every managed app back as it
        // was), to the registry (-Graphics Apply, -Graphics Restore), and lists the apps
        // that take the change when they start again; restarts nothing. Refuses while the
        // GUI runs. Exit code 1 if refused or not everything could be done.
        private static void GraphicsApply(string name) {
            if(GuiRunning("change GPU preferences there, or exit it first and run this again"))
                return;
            if(!GraphicsProfile.Find(Config.GraphicsProfiles, name, out GraphicsProfile target)) {
                Cli.PrintError("No graphics profile named " + name + " (-Graphics lists them; "
                    + GraphicsProfile.DefaultKeyword + " puts every app back as it was)");
                Environment.ExitCode = 1;
                return;
            }
            GraphicsState state = GraphicsMigrate(EcoPlusState.Load(EcoPlusStatePath));
            GraphicsApps graphics = new GraphicsApps(new RegistryGpuPreferences(), state);
            GraphicsResult result = graphics.Apply(target, "-Graphics Apply");

            Console.WriteLine("Applying " + (target != null ? target.Name : GraphicsProfile.WindowsDefaultName));
            if(result.Plan != null && !result.Plan.HasChanges)
                Console.WriteLine("Nothing to change");
            foreach(GraphicsAction a in result.Done)
                Console.WriteLine("  " + a.Describe());
            if(result.Error != null) {
                Cli.PrintError("Graphics apps: " + result.Error);
                Environment.ExitCode = 1;
            }
            List<string> restart = result.Restart;
            if(restart.Count > 0) {
                Console.WriteLine("These apps use the new GPU when they start again (restart them if they run; explorer.exe after signing in again):");
                foreach(string app in restart)
                    Console.WriteLine("  " + app);
            }

            // The profile last applied, once all of it is
            string active = target != null ? target.Name : "";
            if(result.Error == null && Config.GraphicsProfileActive != active) {
                Config.GraphicsProfileActive = active;
                GraphicsSaveConfig();
            }
        }
#endregion

#region Graphics Adopt
        // Makes a profile of the GPU preferences set now and manages its apps, with the
        // originals from a .reg export of the key if given, else their values now
        // (-Graphics Adopt). Changes nothing in the registry. Refuses if the profile
        // exists or the GUI runs.
        private static void GraphicsAdopt(string name, string regFile) {
            if(GuiRunning("exit it first, then run this again"))
                return;
            if(Config.LoadFailed) {
                Cli.PrintError("Graphics apps: " + Config.FilePath + " did not load, so no profile is made");
                Environment.ExitCode = 1;
                return;
            }
            if(!GraphicsProfile.IsValidName(name)) {
                Cli.PrintError("Not a profile name: " + name);
                Environment.ExitCode = 1;
                return;
            }
            if(GraphicsProfile.Find(Config.GraphicsProfiles, name, out GraphicsProfile _)) {
                Cli.PrintError("A graphics profile named " + name + " exists already");
                Environment.ExitCode = 1;
                return;
            }

            IDictionary<string, string> originals = null;
            if(regFile != null) {
                string error;
                try {
                    originals = RegFile.ReadValues(RegFile.Decode(File.ReadAllBytes(regFile)), RegistryGpuPreferences.KeyPath, out error);
                } catch(Exception e) {
                    error = e.Message;
                }
                if(originals == null) {
                    Cli.PrintError("Could not read " + regFile + ": " + error);
                    Environment.ExitCode = 1;
                    return;
                }
            }

            GraphicsState state = GraphicsMigrate(EcoPlusState.Load(EcoPlusStatePath));
            GraphicsApps graphics = new GraphicsApps(new RegistryGpuPreferences(), state);
            GraphicsProfile profile = graphics.Adopt(name, originals);
            if(profile == null) {
                Cli.PrintError("Graphics apps: " + graphics.LastError);
                Environment.ExitCode = 1;
                return;
            }
            Console.WriteLine("Profile " + profile.Name + ": " + profile.Apps.Count + " app(s), originals from "
                + (originals == null ? "their values now" : regFile));
            foreach(GraphicsProfileApp app in profile.Apps)
                Console.WriteLine("  " + GpuValue.Name(app.Choice) + ": " + app.Key + ", original "
                    + GraphicsApps.Show(state.Managed.TryGetValue(app.Key, out GraphicsManaged m) ? m.Original : null));

            // As the registry holds it now, it is the profile last applied
            Config.GraphicsProfiles[profile.Name] = profile;
            Config.GraphicsProfileActive = profile.Name;
            if(!GraphicsSaveConfig())
                Cli.PrintError("Graphics apps: its apps are managed (see -Graphics; -Graphics Restore puts them back), but the profile is not saved");
        }
#endregion

    }

}
