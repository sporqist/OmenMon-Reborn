  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OmenMon.Library;

namespace OmenMon.AppCli {

    // Implements the main operation loop in the application's CLI mode
    // This part covers Eco+: what it saved to restore, and restoring it all without the
    // GUI (for uninstalling). Reads only the state file and Windows settings; no EC or BIOS.
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
            foreach(KeyValuePair<string, string> g in state.SavedGpu)
                Console.WriteLine("GPU preference: " + g.Key + ": original " + RegistryGpuPreferences.Describe(g.Value));
        }
#endregion

#region Eco+ Restore
        // Puts back everything Eco+ changed (-EcoPlus Restore): turbo boost on battery, the
        // refresh rate and the GPU preferences, each only while it is still as Eco+ set
        // it. Refuses while the GUI runs, since it would apply Eco+ again and keeps its
        // own copy of the state. Exit code 1 if refused or the state file is unreadable.
        private static void EcoPlusRestore() {
            if(Mutex.TryOpenExisting(Config.LockPathGui, out Mutex gui)) {
                gui.Dispose();
                Cli.PrintError("OmenMon is running: exit it first (that puts the battery settings back), then run this again");
                Environment.ExitCode = 1;
                return;
            }
            EcoPlusState state = EcoPlusState.Load(EcoPlusStatePath);
            EcoPlus eco = new EcoPlus(new WindowsPowerPlan(), new RegistryGpuPreferences(), new DisplayRate(), state,
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
        }
#endregion

    }

}
