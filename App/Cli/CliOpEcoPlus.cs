  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OmenMon.Library;

namespace OmenMon.AppCli {

    // Implements the main operation loop in the application's CLI mode
    // This part covers Eco+: what it saved to restore (turbo boost on battery, the
    // refresh rate), and restoring it all without the GUI (for uninstalling). Reads only
    // the state file and Windows settings; no EC or BIOS.
    public static partial class CliOp {

        private static string EcoPlusStatePath {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, EcoPlusState.FileName); }
        }

#region Eco+ Status
        // Lists the originals Eco+ keeps (-EcoPlus)
        private static void EcoPlusShow() {
            EcoPlusState state = EcoPlusState.Load(EcoPlusStatePath);
            if(state.LoadError != null)
                Cli.PrintError("Eco+: " + state.LoadError);
            if(state.IsEmpty) {
                Console.WriteLine("Nothing saved: Eco+ has no change to restore");
                return;
            }
            foreach(KeyValuePair<Guid, uint> b in state.SavedBoostDc)
                Console.WriteLine("Turbo boost on battery, scheme " + b.Key.ToString("B") + ": original "
                    + WindowsPowerPlan.BoostModeName(b.Value) + " (" + b.Value + ")");
            if(state.RefreshBefore.HasValue)
                Console.WriteLine("Refresh rate: original " + state.RefreshBefore.Value + " Hz"
                    + (state.RefreshSet > 0 ? ", Eco+ set " + state.RefreshSet + " Hz" : "")
                    + (state.RefreshDisplay != null ? " on " + state.RefreshDisplay : ""));
            if(state.SavedGpu.Count > 0)
                Console.WriteLine(state.SavedGpu.Count + " GPU preference(s) saved by an earlier Eco+: they move to Graphics apps"
                    + " on the next start of OmenMon or -Graphics Apply, Restore or Adopt");
        }
#endregion

#region Eco+ Restore
        // Puts back everything Eco+ changed (-EcoPlus Restore): turbo boost on battery and
        // the refresh rate, each only while it is still as Eco+ set it. GPU preferences
        // are Graphics apps' (-Graphics Restore): any an earlier Eco+ saved are moved there
        // first. Refuses while the GUI runs, since it would apply Eco+ again and keeps its
        // own copy of the state. Exit code 1 if refused or the state file is unreadable.
        private static void EcoPlusRestore() {
            if(GuiRunning("exit it first (that puts the battery settings back), then run this again"))
                return;
            EcoPlusState state = EcoPlusState.Load(EcoPlusStatePath);
            GraphicsState graphics = GraphicsMigrate(state);
            EcoPlus eco = new EcoPlus(new WindowsPowerPlan(), new DisplayRate(), state,
                () => new EcoPlusOptions { Enabled = false });
            if(state.LoadError != null) {
                Cli.PrintError("Eco+: " + state.LoadError);
                Environment.ExitCode = 1;
            }
            string done = eco.RestoreAll();
            if(done != null) {
                EcoPlusLog.Append(state.LogPath, DateTime.Now, "-EcoPlus Restore: " + done);
                Console.WriteLine("Restored: " + done);
            } else if(!state.Blocked)
                Console.WriteLine("Nothing to restore");
            if(eco.LastError != null) {
                Cli.PrintError("Eco+: " + eco.LastError);
                Environment.ExitCode = 1;
            }
            if(!graphics.IsEmpty || graphics.Blocked)
                Console.WriteLine("GPU preferences are not part of Eco+ and were left as they are: "
                    + (graphics.Blocked ? "the Graphics apps state cannot be read"
                        : graphics.Managed.Count + " app(s) managed by Graphics apps") + " (see -Graphics; -Graphics Restore puts them back)");
        }

        // Whether the GUI runs: then a change from here is refused (exit code 1), with
        // what to do about it
        private static bool GuiRunning(string what) {
            if(!Mutex.TryOpenExisting(Config.LockPathGui, out Mutex gui))
                return false;
            gui.Dispose();
            Cli.PrintError("OmenMon is running: " + what);
            Environment.ExitCode = 1;
            return true;
        }
#endregion

    }

}
