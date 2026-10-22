  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Collections.Generic;

namespace OmenMon.Core {

    // The BIOS-only fan check (-FanCheck), the rules without I/O. It replaces the
    // calibration wizard on a BIOS-only board: there is nothing to calibrate (the speed is
    // BIOS 0x2D), so it checks that the fans follow the levels. Five steps from the
    // fan-safety floor to the fan table's top (20/23 ... 60/63 on 8BAC and 8C77), never
    // 0/0 and never 255, each held StepSeconds with a 0x10 keep-alive and every write
    // through the fan-safety guard; 0x2D read every second; it stops at
    // AbortTemperature (or when the temperature is unknown), raises the fans to the table
    // maximum and hands control back to the BIOS.
    //
    // A normal run does not end with the hand-back: the BIOS curve stops both fans at
    // light load (8BAC, 2026-10-22: 0/0 for 57 s and counting at 46 °C once the
    // hand-back level from 60/63 expired). It holds the fan-safety floor instead, with
    // the same keep-alive and supervision, until the OmenMon GUI starts (it takes over
    // with its own fan setting; on Auto it writes nothing, not even its heartbeat, and the
    // floor expires into the BIOS curve ~124 s after the last keep-alive), or Ctrl+C, a
    // key or HoldDefaultSeconds hand back to the BIOS.
    public static class FanCheck {

        // The levels [cpu, gpu], in rpm / 100
        public static readonly byte[][] Steps = {
            new byte[] { 20, 23 },
            new byte[] { 30, 33 },
            new byte[] { 40, 43 },
            new byte[] { 50, 53 },
            new byte[] { 60, 63 } };

        // How long each step is held [s], and how often 0x10 keeps it alive within it
        public const int StepSeconds = 20;
        public const int KeepAliveSeconds = 10;

        // The hottest reading that stops the check (the hardware scripts' guard)
        public const int AbortTemperature = 85;

        // A measured level within this of the target counts as reached
        public const int Tolerance = 1;

        // How many of a step's last samples make up its settled value and jitter
        public const int SettleSamples = 5;

        // How long the floor is held after a normal run [s]: by default, and at most
        // (-FanCheck [<Seconds>]; 0 hands back at once)
        public const int HoldDefaultSeconds = 600;
        public const int HoldMaxSeconds = 3600;

        // -FanCheck's arguments, in any order: the hold [s] (digits only, at most
        // HoldMaxSeconds) and the CSV file; false for anything else
        public static bool TryParseArgs(IList<string> args, out int holdSeconds, out string file) {
            holdSeconds = HoldDefaultSeconds;
            file = null;
            bool haveSeconds = false;
            foreach(string a in args) {
                if(IsDigits(a)) {
                    if(haveSeconds || a.Length > 5)
                        return false;
                    int value = 0;
                    foreach(char ch in a)
                        value = value * 10 + (ch - '0');
                    if(value > HoldMaxSeconds)
                        return false;
                    holdSeconds = value;
                    haveSeconds = true;
                } else if(file == null)
                    file = a;
                else
                    return false;
            }
            return true;
        }

        private static bool IsDigits(string text) {
            if(string.IsNullOrEmpty(text))
                return false;
            foreach(char ch in text)
                if(ch < '0' || ch > '9')
                    return false;
            return true;
        }

        // The hold's length as the hint shows it: "10 min", "90 s"
        public static string HoldText(int seconds) {
            return seconds >= 60 && seconds % 60 == 0 ? seconds / 60 + " min" : seconds + " s";
        }

        // The steps for a board: each level at most the fan table's top for that fan (the
        // firmware does not check bounds). Null without a sane table maximum (1-100 per
        // fan): the check does not run without one.
        public static byte[][] Plan(byte[] tableMaximum) {
            if(tableMaximum == null || tableMaximum.Length < 2 || !PanicLevel.IsComplete(tableMaximum))
                return null;
            var plan = new List<byte[]>();
            foreach(byte[] step in Steps) {
                byte[] level = {
                    step[0] < tableMaximum[0] ? step[0] : tableMaximum[0],
                    step[1] < tableMaximum[1] ? step[1] : tableMaximum[1] };
                if(plan.Count == 0 || plan[plan.Count - 1][0] != level[0] || plan[plan.Count - 1][1] != level[1])
                    plan.Add(level);
            }
            return plan.ToArray();
        }

        // Whether the check must stop: the hottest reading unknown, or at the limit
        public static bool MustAbort(int? hottest) {
            return !hottest.HasValue || hottest.Value >= AbortTemperature;
        }

        // One fan over one step: measured holds 0x2D every second after the write
        // (index 0 = 1 s); target is the level written
        public static FanCheckFanResult Analyze(int target, IReadOnlyList<int> measured) {
            if(measured == null || measured.Count == 0)
                return new FanCheckFanResult(target, null, null, null);
            int? rise = null;
            for(int i = 0; i < measured.Count; i++)
                if(Math.Abs(measured[i] - target) <= Tolerance) {
                    rise = i + 1;
                    break;
                }
            int from = measured.Count > SettleSamples ? measured.Count - SettleSamples : 0;
            var window = new List<int>();
            for(int i = from; i < measured.Count; i++)
                window.Add(measured[i]);
            window.Sort();
            int settled = window[window.Count / 2];
            int jitter = window[window.Count - 1] - window[0];
            return new FanCheckFanResult(target, rise, settled, jitter);
        }

    }

    // How the hold after a normal run ended
    public enum FanCheckHoldEnd {
        None,           // no hold: the run did not end normally
        GuiStarted,     // the OmenMon GUI took over the fans: nothing written after it
        CtrlC,          // handed back
        Key,            // handed back
        Timeout,        // handed back
        Aborted         // the abort path (85 °C, the temperature stopped reading)
    }

    // One fan over one step: the level written, the seconds until 0x2D first came within
    // FanCheck.Tolerance of it (null: never), the median of the last samples and their
    // spread (max - min)
    public readonly struct FanCheckFanResult {

        public readonly int Target;
        public readonly int? RiseSeconds;
        public readonly int? Settled;
        public readonly int? Jitter;

        public FanCheckFanResult(int target, int? riseSeconds, int? settled, int? jitter) {
            this.Target = target;
            this.RiseSeconds = riseSeconds;
            this.Settled = settled;
            this.Jitter = jitter;
        }

    }

}
