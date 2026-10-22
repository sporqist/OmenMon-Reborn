  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;

namespace OmenMon.Core {

    // How a board's fans are controlled
    public enum FanControlKind {

        // A model whose EC fan registers are used as configured (the legacy path): rate,
        // countdown, manual, mode and switch registers are read and, depending on the
        // settings, written
        Ec,

        // BIOS only: levels through 0x10/0x2E (via the fan-safety guard), the fan speed is
        // the 0x2D level x 100, temperatures come from BIOS 0x23 and the EC registers the
        // model names. No EC fan register is read or written. For every board without a
        // verified EC map: on 8C77 (measured) the default fan registers point into ID,
        // serial and SMBus bytes.
        Bios

    }

    // The decisions of BIOS-only fan control, without I/O
    public static class BiosFanControl {

        // How long a fan level holds after the last 0x10/0x2E (8BAC and 8C77: ~124 s,
        // see BoardProfile.LevelExpiry). Used for the countdown a BIOS-only board shows.
        public static readonly TimeSpan DefaultLevelHold = TimeSpan.FromSeconds(124);

        // 0x2D reports the level in units of 100 rpm; on 8BAC it ramps over ~15 s after a
        // set and jitters at steady state, so it behaves like a measured speed
        public const int RpmPerLevel = 100;

        // The speed shown for a 0x2D level [rpm]
        public static int Rpm(int level) {
            return level <= 0 ? 0 : level * RpmPerLevel;
        }

        // Seconds a held level has left: from the last 0x10/0x2E OmenMon sent; 0 if it
        // holds none (null) or the time is up
        public static int HoldSecondsLeft(DateTime? lastRefresh, DateTime now, TimeSpan hold) {
            if(!lastRefresh.HasValue)
                return 0;
            double left = (lastRefresh.Value + hold - now).TotalSeconds;
            return left <= 0 ? 0 : (int) Math.Ceiling(left);
        }

        // Whether the level OmenMon holds has both fans off (0/0)
        public static bool IsOff(byte[] written) {
            return written != null && written.Length >= 2 && written[0] == 0 && written[1] == 0;
        }

    }

}
