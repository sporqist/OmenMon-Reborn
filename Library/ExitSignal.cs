  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace OmenMon.Library {

    // Lets OmenMon -Exit ask the running GUI to exit the way the tray's Exit does (the
    // fans handed back to the BIOS, Eco+ put back), for installers and scripts: closing
    // its window only hides it in the tray. A named event the GUI waits on; only
    // administrators and SYSTEM may set it, so a program without elevation cannot stop
    // the fan controller.
    public static class ExitSignal {

        // The GUI's event, or null if it cannot be made (then -Exit cannot reach it)
        public static EventWaitHandle Create(string name) {
            try {
                EventWaitHandleSecurity security = new EventWaitHandleSecurity();
                foreach(WellKnownSidType sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                    security.AddAccessRule(new EventWaitHandleAccessRule(
                        new SecurityIdentifier(sid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
#if NETFRAMEWORK
                EventWaitHandle e = new EventWaitHandle(false, EventResetMode.AutoReset, name, out bool created, security);
#else
                EventWaitHandle e = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out bool created, security);
#endif
                if(created)
                    return e;
                // Made by someone else first: its ACL is not ours to trust
                e.Dispose();
            } catch {
            }
            return null;
        }

        // Sets the GUI's event; false if no GUI waits on it or this process may not set it
        public static bool Signal(string name) {
            try {
#if NETFRAMEWORK
                using(EventWaitHandle e = EventWaitHandle.OpenExisting(name, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize))
#else
                using(EventWaitHandle e = EventWaitHandleAcl.OpenExisting(name, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize))
#endif
                    return e.Set();
            } catch {
                return false;
            }
        }

        // Waits until isRunning says no more, polling; true if it did within the timeout
        public static bool WaitGone(Func<bool> isRunning, TimeSpan timeout, Action<TimeSpan> sleep = null, Func<DateTime> clock = null) {
            sleep = sleep ?? (t => Thread.Sleep(t));
            clock = clock ?? (() => DateTime.UtcNow);
            DateTime deadline = clock() + timeout;
            while(isRunning()) {
                if(clock() >= deadline)
                    return false;
                sleep(TimeSpan.FromMilliseconds(500));
            }
            return true;
        }

    }

}
