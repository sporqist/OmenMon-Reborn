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
        public byte[] TableMaximum;
        public List<FanCheckSample> Samples = new List<FanCheckSample>();
        public List<FanCheckStep> Steps = new List<FanCheckStep>();
    }

    // The BIOS-only fan check (FanCheck has the rules). Every level goes through the fan
    // array, so through the fan-safety guard (SafeLevel): the steps, the keep-alive (0x10
    // only), supervision, the abort level (the table maximum). It reads nothing from the
    // EC but the temperatures the platform reads anyway (no dump), and it always ends by
    // handing control back to the BIOS (the existing hand-back: a safe last level, then
    // silence). waitSeconds waits (the CLI: real time; the tests: simulated).
    public static class FanCheckRunner {

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
            Action<string> progress = null, CancellationToken cancel = default(CancellationToken)) {

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
            } finally {
                fans.Release();
                result.HandedBack = true;
            }

            return result;
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
            sb.AppendLine(r.HandedBack
                ? "Control handed back to the BIOS (the last level holds about 2 minutes, then the BIOS curve runs)"
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
