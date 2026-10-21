  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Threading;
using OmenMon.Library;

namespace OmenMon.AppCli {

    // Implements the main operation loop in the application's CLI mode
    // This part covers -Exit: asking the running GUI to exit the way its tray Exit does
    // (fans handed back to the BIOS, Eco+ put back), for installers and scripts. Closing
    // the GUI's window only hides it in the tray, and ending its process would skip both.
    public static partial class CliOp {

        private static bool GuiRunning() {
            if(!Mutex.TryOpenExisting(Config.LockPathGui, out Mutex gui))
                return false;
            gui.Dispose();
            return true;
        }

        // Exit code 0 if no GUI runs any more, 1 if it could not be asked (not run as
        // administrator, or a GUI too old to listen) or is still running after the wait
        private static void ExitGui(int seconds) {
            if(!GuiRunning()) {
                Console.WriteLine("OmenMon is not running");
                return;
            }
            if(!ExitSignal.Signal(Config.LockPathGuiExit)) {
                Cli.PrintError("Could not ask OmenMon to exit: run this as administrator (or exit it from its tray icon)");
                Environment.ExitCode = 1;
                return;
            }
            if(ExitSignal.WaitGone(GuiRunning, TimeSpan.FromSeconds(seconds)))
                Console.WriteLine("OmenMon has exited");
            else {
                Cli.PrintError("OmenMon was asked to exit but still runs after " + seconds + " s");
                Environment.ExitCode = 1;
            }
        }

    }

}
