  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Collections.Generic;

namespace OmenMon.Core {

    // The thermal-panic level: the highest level per fan in the BIOS fan table (0x2F),
    // which is what the firmware itself runs at the top of its curve (8BAC: 60/63). The
    // fan array (in the application) reads the table and passes its rows in.
    public static class PanicLevel {

        // The highest sane level (1-100) per fan in the fan table rows [CPU, GPU]; 0 for a
        // fan without one (no rows, a failed read, or only out-of-range values)
        public static byte[] TableMaximum(IEnumerable<(byte Cpu, byte Gpu)> rows) {
            byte[] table = new byte[2];
            foreach((byte Cpu, byte Gpu) row in rows) {
                if(row.Cpu <= 100 && row.Cpu > table[0]) table[0] = row.Cpu;
                if(row.Gpu <= 100 && row.Gpu > table[1]) table[1] = row.Gpu;
            }
            return table;
        }

        // Whether the table gave a level for each fan (only then is it worth keeping)
        public static bool IsComplete(byte[] tableMaximum) {
            return tableMaximum[0] > 0 && tableMaximum[1] > 0;
        }

        // The panic level: the table maximum, or for a fan without one the fallback:
        // configuredMax (Config.FanLevelMax, clamped to 1-100), but at least the warm
        // safety curve's top step (60/63 by default), the level the guard gives an
        // unknown temperature
        public static byte[] Choose(byte[] tableMaximum, int configuredMax, FanSafetyPolicy policy) {
            byte max = (byte) Math.Max(1, Math.Min(100, configuredMax));
            byte[] level = new byte[2];
            for(int i = 0; i < 2; i++)
                level[i] = tableMaximum[i] > 0 ? tableMaximum[i] : Math.Max(max, policy.ManualMinimum(i, null));
            return level;
        }

    }

}
