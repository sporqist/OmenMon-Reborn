  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;

namespace OmenMon.Hardware.Platform {

    // Process-wide last-chance hand-back of fan control to the BIOS, for exit and
    // crash paths that cannot reach the GUI objects. The GUI registers what to do.
    public static class FanHandBack {

        private static volatile Action action;

        public static void Register(Action handBack) {
            action = handBack;
        }

        // Runs the registered hand-back on a separate thread and waits at most
        // timeoutMs for it, so a wedged BIOS call cannot hang exit or crash handling.
        // Returns true if it completed in time.
        public static bool Run(int timeoutMs) {
            Action a = action;
            if(a == null)
                return true;
            try {
                var worker = new System.Threading.Thread(() => { try { a(); } catch { } }) {
                    IsBackground = true,
                    Name = "OmenMonFanHandBack" };
                worker.Start();
                return worker.Join(timeoutMs);
            } catch {
                return false;
            }
        }

    }

}
