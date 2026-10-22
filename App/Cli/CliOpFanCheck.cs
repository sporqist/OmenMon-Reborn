  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using OmenMon.Hardware.Platform;
using OmenMon.Library;

namespace OmenMon.AppCli {

    // Implements the main operation loop in the application's CLI mode
    // This part covers -FanCheck: the BIOS-only fan check (FanCheckRunner), which takes
    // the calibration wizard's place on a BIOS-only board. It needs the fans to itself, so
    // it does not run while the OmenMon GUI runs; Ctrl+C stops it. After a normal run it
    // holds the floor until the GUI starts (then it exits without another write) or until
    // Ctrl+C, a key or the hold's time hands control back to the BIOS.
    public static partial class CliOp {

        public static string FanCheckDefaultFileName(DateTime time) {
            return "OmenMon-FanCheck-" + time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv";
        }

        private static void FanCheckRun(int holdSeconds, string file) {

            if(GuiRunning()) {
                Cli.PrintError("OmenMon is running and controls the fans: exit it first (OmenMon -Exit)");
                Environment.ExitCode = 1;
                return;
            }

            file = Path.GetFullPath(file ?? FanCheckDefaultFileName(DateTime.Now));

            Hw.BiosInit();
            Hw.EcInit();
            Platform platform = new Platform();

            string refusal = FanCheckRunner.Refusal(platform.Fans);
            if(refusal != null) {
                Cli.PrintError("Fan check not run: " + refusal);
                Environment.ExitCode = 1;
                return;
            }

            using(var stop = new CancellationTokenSource()) {
                ConsoleCancelEventHandler onCancel = (sender, eventArgs) => {
                    eventArgs.Cancel = true; // End the check, not the process: the hand-back follows
                    stop.Cancel();
                };
                Console.CancelKeyPress += onCancel;
                Console.WriteLine("Fan check: 5 steps of " + OmenMon.Core.FanCheck.StepSeconds
                    + " s, stops at " + OmenMon.Core.FanCheck.AbortTemperature + " °C (Ctrl+C stops it; the fans go back to the BIOS),"
                    + " then holds the floor level until OmenMon starts");

                FanCheckResult result;
                try {
                    result = FanCheckRunner.Run(platform,
                        seconds => stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds)),
                        Console.WriteLine, stop.Token,
                        new FanCheckHold { Seconds = holdSeconds, GuiStarted = GuiRunning, KeyPressed = FanCheckKeyPressed });
                } finally {
                    Console.CancelKeyPress -= onCancel;
                }

                Console.WriteLine();
                Console.Write(FanCheckRunner.Report(result));
                if(result.Refusal != null) {
                    Environment.ExitCode = 1;
                    return;
                }
                try {
                    File.WriteAllText(file, FanCheckRunner.Csv(result), new UTF8Encoding(false));
                    Console.WriteLine("Samples: " + file);
                } catch(Exception e) {
                    Cli.PrintError("Could not write " + file + ": " + e.Message);
                    Environment.ExitCode = 1;
                }
                if(result.Aborted)
                    Environment.ExitCode = 1;
            }
        }

        // Whether a key was pressed since the last call, consuming every key waiting
        // (never with no console to read keys from)
        private static bool FanCheckKeyPressed() {
            bool pressed = false;
            try {
                if(Console.IsInputRedirected)
                    return false;
                while(Console.KeyAvailable) {
                    Console.ReadKey(true);
                    pressed = true;
                }
            } catch(InvalidOperationException) { }
            return pressed;
        }

    }

}
