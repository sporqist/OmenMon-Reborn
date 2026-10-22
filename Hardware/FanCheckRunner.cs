  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using OmenMon.Core;

namespace OmenMon.Hardware.Platform {

    // One second of the fan check
    public sealed class FanCheckSample {
        public int Step;            // 1-based
        public int Second;          // 1-based, within the step
        public byte[] Target;       // the step's level
        public byte[] Written;      // what the guard wrote (null: none held)
        public byte[] Measured;     // BIOS 0x2D (null: the read failed)
        public int? Cpu;            // the CPU reading [°C], null if none
        public string Gpu;          // the GPU: a temperature, "parked" or "unknown"
    }

    // One step of the fan check, per fan
    public sealed class FanCheckStep {
        public int Step;
        public byte[] Target;
        public byte[] Written;
        public FanCheckFanResult[] Fan;
        public int? CpuMin, CpuMax;
    }

    // What the fan check did
    public sealed class FanCheckResult {
        public string Refusal;                  // non-null: nothing was written
        public bool Aborted;
        public string AbortReason;
        public bool Raised;                     // the abort raised the fans to the table maximum
        public bool HandedBack;
        public FanCheckHoldEnd HoldEnd;         // how the hold after a normal run ended (None: no hold)
        public int HoldSeconds;                 // how long it lasted
        public byte[] HoldLevel;                // what the floor request wrote (the guard may raise it)
        public byte[] TableMaximum;
        public List<FanCheckSample> Samples = new List<FanCheckSample>();
        public List<FanCheckStep> Steps = new List<FanCheckStep>();
    }

    // The hold after a normal run: how long until the hand-back, and what ends it early
    public sealed class FanCheckHold {
        public int Seconds = FanCheck.HoldDefaultSeconds;
        public Func<bool> GuiStarted;           // whether the OmenMon GUI runs (null: never)
        public Func<bool> KeyPressed;           // whether a key was pressed since the last call,
                                                // consuming them (null: never)
    }

    // The BIOS-only fan check (FanCheck has the rules). Every level goes through the fan
    // array, so through the fan-safety guard (SafeLevel): the steps, the keep-alive (0x10
    // only), supervision, the abort level (the table maximum), the floor held after the
    // steps. It reads nothing from the EC but the temperatures the platform reads anyway
    // (no dump). It ends by handing control back to the BIOS (the existing hand-back: a
    // safe last level, then silence), except when the OmenMon GUI starts during the hold:
    // the GUI then has the fans, and nothing more is written. waitSeconds waits (the CLI:
    // real time; the tests: simulated).
    public static class FanCheckRunner {

        // Printed with every hand-back that did not raise the fans
        public const string HandBackWarning = "Warning: the BIOS curve may stop both fans at light load"
            + " (8BAC: 0/0 at 46 °C once the last level expired); start OmenMon to keep them turning";

        // Why the check does not run on these fans, or null
        public static string Refusal(IFanArray fans) {
            if(fans == null)
                return "No fan interface";
            if(fans.Control != FanControlKind.Bios)
                return "This board's fans are controlled through its EC registers: use the calibration wizard (Auto-Calibrate & Diagnose) instead";
            if(fans.Guard == null)
                return "No fan-safety guard: not run";
            return null;
        }

        public static FanCheckResult Run(Platform platform, Action<int> waitSeconds,
            Action<string> progress = null, CancellationToken cancel = default(CancellationToken),
            FanCheckHold hold = null) {

            var result = new FanCheckResult();
            progress = progress ?? (s => { });
            IFanArray fans = platform?.Fans;

            result.Refusal = Refusal(fans);
            if(result.Refusal != null)
                return result;
            FanSafetyGuard guard = fans.Guard;

            result.TableMaximum = fans.GetTableMaximum();
            byte[][] plan = FanCheck.Plan(result.TableMaximum);
            if(plan == null) {
                result.Refusal = "The BIOS fan table (0x2F) cannot be read: without its top row the steps have no upper bound";
                return result;
            }

            platform.UpdateTemperature();
            int? hottest = guard.Hottest();
            if(FanCheck.MustAbort(hottest)) {
                result.Refusal = hottest.HasValue
                    ? "Too hot to start: " + hottest.Value + " °C (the check stops at " + FanCheck.AbortTemperature + " °C)"
                    : "The temperature does not read: not started";
                return result;
            }

            bool handBack = true;
            try {
                for(int s = 0; s < plan.Length && !result.Aborted; s++) {

                    byte[] target = plan[s];
                    progress("Step " + (s + 1) + "/" + plan.Length + ": " + target[0] + "/" + target[1]);
                    fans.SetLevels(new byte[] { target[0], target[1] });
                    var stepSamples = new List<FanCheckSample>();

                    for(int second = 1; second <= FanCheck.StepSeconds; second++) {

                        if(cancel.IsCancellationRequested) {
                            result.Aborted = true;
                            result.AbortReason = "Cancelled";
                            break;
                        }
                        waitSeconds(1);

                        platform.UpdateTemperature();
                        if(second % FanCheck.KeepAliveSeconds == 0)
                            fans.Arm();
                        fans.Supervise();

                        hottest = guard.Hottest();
                        FanCheckSample sample = Sample(platform, guard, s + 1, second, target);
                        result.Samples.Add(sample);
                        stepSamples.Add(sample);

                        if(FanCheck.MustAbort(hottest)) {
                            Abort(result, fans, hottest.HasValue
                                ? FanCheck.AbortTemperature + " °C reached (" + hottest.Value + " °C)"
                                : "The temperature stopped reading");
                            break;
                        }
                    }

                    result.Steps.Add(Summarize(s + 1, target, stepSamples));
                }

                if(!result.Aborted)
                    handBack = Hold(result, platform, waitSeconds, progress, cancel, hold ?? new FanCheckHold());

            } finally {
                if(handBack) {
                    fans.Release();
                    result.HandedBack = true;
                }
            }

            return result;
        }

        // Holds the floor after a normal run (FanCheck says why) with the steps' keep-alive
        // and supervision. False when the OmenMon GUI took over (nothing may be written any
        // more), true when control goes back to the BIOS: Ctrl+C, a key, the time is up,
        // or the abort path (the fans raised first).
        private static bool Hold(FanCheckResult result, Platform platform, Action<int> waitSeconds,
            Action<string> progress, CancellationToken cancel, FanCheckHold hold) {

            IFanArray fans = platform.Fans;
            FanSafetyGuard guard = fans.Guard;
            Func<bool> guiStarted = hold.GuiStarted ?? (() => false);
            Func<bool> keyPressed = hold.KeyPressed ?? (() => false);

            if(guiStarted()) {
                result.HoldEnd = FanCheckHoldEnd.GuiStarted;
                progress("OmenMon is running and has the fans: exiting without another write");
                return false;
            }
            if(hold.Seconds <= 0 || cancel.IsCancellationRequested) {
                result.HoldEnd = hold.Seconds <= 0 ? FanCheckHoldEnd.Timeout : FanCheckHoldEnd.CtrlC;
                return true;
            }

            // The floor: the policy's minimum per fan (10-100 by the configuration), so
            // never 0/0 or 255; the guard raises it while warm
            byte[] floor = { guard.Policy.Floor(0), guard.Policy.Floor(1) };
            fans.SetLevels(new byte[] { floor[0], floor[1] });
            result.HoldLevel = guard.Written;
            keyPressed();   // A key pressed during the steps does not end the hold
            progress("Fan check done. Holding the floor level " + floor[0] + "/" + floor[1]
                + (result.HoldLevel != null && (result.HoldLevel[0] != floor[0] || result.HoldLevel[1] != floor[1])
                    ? " (raised to " + result.HoldLevel[0] + "/" + result.HoldLevel[1] + " while warm)" : "")
                + " until OmenMon starts (start it now) or Ctrl+C / any key / "
                + FanCheck.HoldText(hold.Seconds) + " to hand back to the BIOS.");

            for(int second = 1; ; second++) {

                waitSeconds(1);
                result.HoldSeconds = second;

                // The GUI has the fans now: not even a keep-alive
                if(guiStarted()) {
                    result.HoldEnd = FanCheckHoldEnd.GuiStarted;
                    progress("OmenMon started and takes over the fans after " + second + " s: exiting without another write");
                    return false;
                }

                platform.UpdateTemperature();
                int? hottest = guard.Hottest();
                if(FanCheck.MustAbort(hottest)) {
                    Abort(result, fans, hottest.HasValue
                        ? FanCheck.AbortTemperature + " °C reached during the hold (" + hottest.Value + " °C)"
                        : "The temperature stopped reading during the hold");
                    result.HoldEnd = FanCheckHoldEnd.Aborted;
                    return true;
                }

                FanCheckHoldEnd end = cancel.IsCancellationRequested ? FanCheckHoldEnd.CtrlC
                    : keyPressed() ? FanCheckHoldEnd.Key
                    : second >= hold.Seconds ? FanCheckHoldEnd.Timeout
                    : FanCheckHoldEnd.None;
                if(end != FanCheckHoldEnd.None) {
                    result.HoldEnd = end;
                    progress("Hold ended after " + second + " s (" + HoldEndText(end) + "): handing the fans back to the BIOS");
                    return true;   // The report has the warning
                }

                if(second % FanCheck.KeepAliveSeconds == 0)
                    fans.Arm();
                fans.Supervise();
            }
        }

        private static string HoldEndText(FanCheckHoldEnd end) {
            switch(end) {
                case FanCheckHoldEnd.GuiStarted: return "OmenMon started";
                case FanCheckHoldEnd.CtrlC: return "Ctrl+C";
                case FanCheckHoldEnd.Key: return "a key";
                case FanCheckHoldEnd.Timeout: return "time up";
                case FanCheckHoldEnd.Aborted: return "the check stopped";
                default: return "-";
            }
        }

        // Stops the check: the fans to the fan table's maximum (through the guard), then
        // the caller's hand-back leaves that as the last level
        private static void Abort(FanCheckResult result, IFanArray fans, string reason) {
            result.Aborted = true;
            result.AbortReason = reason;
            result.Raised = true;
            fans.Arm();
            fans.SetLevels(fans.GetPanicLevel());
        }

        private static FanCheckSample Sample(Platform platform, FanSafetyGuard guard, int step, int second, byte[] target) {
            byte[] measured = null;
            try { measured = platform.Fans.GetLevels(); } catch { }
            SensorReading cpu = platform.GetCpuReading(false);
            GpuReading gpu = platform.GetGpuReading(false);
            DateTime now = guard.Now;
            string gpuText;
            switch(gpu.StateAt(now, guard.Policy.ReadingMaxAge)) {
                case GpuState.Fresh: gpuText = gpu.Last.Value.ToString(CultureInfo.InvariantCulture); break;
                case GpuState.Parked: gpuText = "parked"; break;
                default: gpuText = "unknown"; break;
            }
            return new FanCheckSample {
                Step = step,
                Second = second,
                Target = target,
                Written = guard.Written,
                Measured = measured != null && measured.Length >= 2 ? new byte[] { measured[0], measured[1] } : null,
                Cpu = cpu.IsFresh(now, guard.Policy.ReadingMaxAge) ? (int?) cpu.Value : null,
                Gpu = gpuText };
        }

        private static FanCheckStep Summarize(int step, byte[] target, List<FanCheckSample> samples) {
            byte[] written = samples.Count > 0 ? samples[samples.Count - 1].Written : null;
            var fan = new FanCheckFanResult[2];
            for(int i = 0; i < 2; i++) {
                int goal = written != null ? written[i] : target[i];
                var measured = samples.Where(x => x.Measured != null).Select(x => (int) x.Measured[i]).ToList();
                fan[i] = FanCheck.Analyze(goal, measured);
            }
            var cpu = samples.Where(x => x.Cpu.HasValue).Select(x => x.Cpu.Value).ToList();
            return new FanCheckStep {
                Step = step, Target = target, Written = written, Fan = fan,
                CpuMin = cpu.Count > 0 ? cpu.Min() : (int?) null,
                CpuMax = cpu.Count > 0 ? cpu.Max() : (int?) null };
        }

        // The short report
        public static string Report(FanCheckResult r) {
            var sb = new StringBuilder();
            if(r.Refusal != null) {
                sb.AppendLine("Fan check not run: " + r.Refusal);
                return sb.ToString();
            }
            sb.AppendLine("Fan check (BIOS 0x2D every second, levels in rpm/100; fan table top "
                + r.TableMaximum[0] + "/" + r.TableMaximum[1] + ")");
            sb.AppendLine("Step  Target  Written  CPU fan: rise/settled/jitter  GPU fan: rise/settled/jitter  CPU °C");
            foreach(FanCheckStep s in r.Steps)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,4}  {1,6}  {2,7}  {3,28}  {4,28}  {5}",
                    s.Step, s.Target[0] + "/" + s.Target[1],
                    s.Written != null ? s.Written[0] + "/" + s.Written[1] : "-",
                    Fan(s.Fan[0]), Fan(s.Fan[1]),
                    s.CpuMin.HasValue ? s.CpuMin + "-" + s.CpuMax : "-"));
            if(r.Aborted)
                sb.AppendLine("Stopped: " + r.AbortReason + (r.Raised ? "; the fans were raised to the table maximum" : ""));
            if(r.HoldEnd != FanCheckHoldEnd.None)
                sb.AppendLine("Hold: " + (r.HoldLevel != null ? r.HoldLevel[0] + "/" + r.HoldLevel[1] : "-")
                    + " for " + r.HoldSeconds + " s, ended: " + HoldEndText(r.HoldEnd));
            if(r.HandedBack) {
                sb.AppendLine("Control handed back to the BIOS (the last level holds about 2 minutes, then the BIOS curve runs)");
                if(!r.Raised)
                    sb.AppendLine(HandBackWarning);
            } else
                sb.AppendLine(r.HoldEnd == FanCheckHoldEnd.GuiStarted
                    ? "Control left to OmenMon (nothing written after it started)"
                    : "Control NOT handed back");
            return sb.ToString();
        }

        private static string Fan(FanCheckFanResult f) {
            return (f.RiseSeconds.HasValue ? f.RiseSeconds + " s" : "not reached")
                + " / " + (f.Settled.HasValue ? f.Settled.ToString() : "-")
                + " / " + (f.Jitter.HasValue ? f.Jitter.ToString() : "-");
        }

        // The samples as CSV
        public static string Csv(FanCheckResult r) {
            var sb = new StringBuilder();
            sb.AppendLine("step,second,target_cpu,target_gpu,written_cpu,written_gpu,measured_cpu,measured_gpu,cpu_c,gpu_c");
            foreach(FanCheckSample s in r.Samples)
                sb.AppendLine(string.Join(",",
                    s.Step.ToString(CultureInfo.InvariantCulture),
                    s.Second.ToString(CultureInfo.InvariantCulture),
                    s.Target[0].ToString(CultureInfo.InvariantCulture),
                    s.Target[1].ToString(CultureInfo.InvariantCulture),
                    s.Written != null ? s.Written[0].ToString(CultureInfo.InvariantCulture) : "",
                    s.Written != null ? s.Written[1].ToString(CultureInfo.InvariantCulture) : "",
                    s.Measured != null ? s.Measured[0].ToString(CultureInfo.InvariantCulture) : "",
                    s.Measured != null ? s.Measured[1].ToString(CultureInfo.InvariantCulture) : "",
                    s.Cpu.HasValue ? s.Cpu.Value.ToString(CultureInfo.InvariantCulture) : "",
                    s.Gpu ?? ""));
            return sb.ToString();
        }

    }

}
