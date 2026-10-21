  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Threading;

namespace OmenMon.Hardware.Platform {

    // Process-wide last-chance hand-back of fan control to the BIOS, for exit and
    // crash paths that cannot reach the GUI objects. The GUI registers what to do.
    public static class FanHandBack {

        // The action and the lock it should run under, swapped as one
        private sealed class Registration {
            public Action Action;
            public object Lock;
        }

        private static volatile Registration registration;

        // Registers the hand-back. With hardwareLock, Run() takes that lock for it if it
        // can get it in time (see LockWaitMs), and proceeds without it otherwise: a
        // crashed thread may still hold it, and handing back matters more.
        public static void Register(Action handBack, object hardwareLock = null) {
            registration = handBack == null ? null : new Registration { Action = handBack, Lock = hardwareLock };
        }

        // How long Run() waits for the hardware lock out of its time budget: a third, at
        // most 400 ms. The rest (about 1 s of 1500 ms) is for the hand-back itself, the
        // 0x10 arm call and two 0x2E writes. Waiting longer than the budget meant that
        // a held lock made Run() give up before the hand-back ever started.
        public static int LockWaitMs(int timeoutMs) {
            return Math.Max(0, Math.Min(400, timeoutMs / 3));
        }

        // Runs the registered hand-back on a separate thread and waits at most
        // timeoutMs for it, so a wedged BIOS call cannot hang exit or crash handling.
        // Returns true if it completed in time.
        public static bool Run(int timeoutMs) {
            Registration r = registration;
            if(r == null)
                return true;
            int lockWait = LockWaitMs(timeoutMs);
            try {
                var worker = new Thread(() => {
                    bool locked = false;
                    try {
                        if(r.Lock != null)
                            locked = Monitor.TryEnter(r.Lock, lockWait);
                        r.Action();
                    } catch {
                    } finally {
                        if(locked)
                            Monitor.Exit(r.Lock);
                    }
                }) {
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
