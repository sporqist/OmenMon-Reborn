  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Core;
using OmenMon.Hardware.Platform;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // R4: the BIOS-only fan check (-FanCheck) on the simulated BIOS: the steps through
    // the guard, the 0x10 keep-alive, the abort at 85 °C or an unknown temperature, the
    // floor held after a normal run (until the GUI starts, or Ctrl+C, a key or the time
    // hands back), never 0/0 or 255, and nothing written to the EC
    [Collection(HardwareStateCollection.Name)]
    public class BiosOnlyFanCheckTests {

        private static readonly byte[] Table8Bac = { 2, 3, 20, 23, 40, 40, 43, 60, 60, 63, 80 };

        private static TestRig Rig() {
            var rig = new TestRig(preset: BiosOnlySessionTests.Board8C77());
            BiosOnlySessionTests.Fill8C77Image(rig.Ec, 0x00);
            rig.Bios.FanTable = Table8Bac;
            rig.SetCpuTemperature(45);
            rig.Ec.GpuTemperature = 0;   // parked
            return rig;
        }

        private static void AssertNeverOffOr255(TestRig rig) {
            Assert.All(rig.Bios.LevelWrites, l => {
                Assert.False(l[0] == 0 && l[1] == 0, "0/0 written");
                Assert.NotEqual(0xFF, l[0]);
                Assert.NotEqual(0xFF, l[1]);
            });
        }

        [Fact]
        public void Rules_PlanAbortAnalyze() {
            Assert.Equal(new[] { "20/23", "30/33", "40/43", "50/53", "60/63" },
                FanCheck.Plan(new byte[] { 60, 63 })!.Select(l => l[0] + "/" + l[1]));
            Assert.Equal(new[] { "20/23", "30/33", "40/43", "45/50" },   // capped, the repeat dropped
                FanCheck.Plan(new byte[] { 45, 50 })!.Select(l => l[0] + "/" + l[1]));
            Assert.Null(FanCheck.Plan(null!));
            Assert.Null(FanCheck.Plan(new byte[] { 0, 63 }));

            Assert.True(FanCheck.MustAbort(null));
            Assert.True(FanCheck.MustAbort(85));
            Assert.False(FanCheck.MustAbort(84));

            FanCheckFanResult r = FanCheck.Analyze(30, new[] { 20, 25, 29, 30, 30, 31, 30 });
            Assert.Equal(3, r.RiseSeconds);   // 29 is within 1
            Assert.Equal(30, r.Settled);
            Assert.Equal(2, r.Jitter);
            Assert.Null(FanCheck.Analyze(60, new[] { 20, 30, 40 }).RiseSeconds);

            // -FanCheck [<Seconds>] [<File.csv>], in any order
            Assert.True(FanCheck.TryParseArgs(new string[0], out int hold, out string? file));
            Assert.Equal(FanCheck.HoldDefaultSeconds, hold);
            Assert.Null(file);
            Assert.True(FanCheck.TryParseArgs(new[] { "a.csv", "90" }, out hold, out file));
            Assert.Equal(90, hold);
            Assert.Equal("a.csv", file);
            Assert.True(FanCheck.TryParseArgs(new[] { "0" }, out hold, out _));
            Assert.Equal(0, hold);
            Assert.False(FanCheck.TryParseArgs(new[] { "3601" }, out _, out _));
            Assert.False(FanCheck.TryParseArgs(new[] { "99999999999" }, out _, out _));
            Assert.False(FanCheck.TryParseArgs(new[] { "60", "30" }, out _, out _));
            Assert.False(FanCheck.TryParseArgs(new[] { "a.csv", "b.csv" }, out _, out _));
            Assert.Equal("10 min", FanCheck.HoldText(600));
            Assert.Equal("90 s", FanCheck.HoldText(90));
        }

        // The whole check: five steps in order through the guard, 0x10 within each step,
        // 0x2D every second, then (no hold) the hand-back; no EC write, no 0/0, no 255
        [Fact]
        public void Run_StepsKeepAliveHandBack_NoEcWrite() {
            using TestRig rig = Rig();
            rig.Bios.Calls.Clear();

            FanCheckResult r = FanCheckRunner.Run(rig.Platform, s => rig.Advance(s), null, TestContext.Current.CancellationToken,
                new FanCheckHold { Seconds = 0 });

            Assert.Null(r.Refusal);
            Assert.False(r.Aborted);
            Assert.True(r.HandedBack);
            Assert.Equal(FanCheckHoldEnd.Timeout, r.HoldEnd);
            Assert.Equal(0, r.HoldSeconds);
            Assert.Equal(5 * FanCheck.StepSeconds, r.Samples.Count);
            Assert.Equal(5, r.Steps.Count);

            var writes = rig.Bios.LevelWrites.Select(l => l[0] + "/" + l[1]).ToList();
            Assert.Equal(new[] { "20/23", "30/33", "40/43", "50/53", "60/63", "60/63" }, writes);   // the last: the hand-back
            AssertNeverOffOr255(rig);

            // Within each step 0x10 keeps the level alive (besides the one before each 0x2E)
            var types = rig.Bios.Calls.Select(c => c.Type).ToList();
            int[] sets = types.Select((t, i) => (t, i)).Where(x => x.t == 0x2E).Select(x => x.i).ToArray();
            for(int s = 0; s < 4; s++)
                Assert.True(types.Skip(sets[s] + 1).Take(sets[s + 1] - sets[s] - 2).Count(t => t == 0x10) >= 1,
                    "no keep-alive in step " + (s + 1));
            Assert.True(rig.Bios.Count(0x2D) >= 5 * FanCheck.StepSeconds);

            // Handed back: OmenMon holds nothing, the heartbeat stays quiet for the expiry
            Assert.False(rig.Platform.Fans.Guard!.HoldsLevel);
            Assert.False(rig.Platform.Fans.HeartbeatAllowed);

            // The simulated BIOS applies a level at once: reached in 1 s, no jitter
            Assert.All(r.Steps, s => {
                Assert.Equal(1, s.Fan[0].RiseSeconds);
                Assert.Equal(s.Target[0], s.Fan[0].Settled);
                Assert.Equal(0, s.Fan[1].Jitter);
            });

            Assert.Empty(rig.Ec.Writes);
            Assert.DoesNotContain((byte) 0x14, rig.Ec.Reads);
            Assert.Equal(0, rig.Bios.Count(0x1A));
            Assert.Equal(0, rig.Bios.Count(0x27));

            string csv = FanCheckRunner.Csv(r);
            Assert.StartsWith("step,second,target_cpu,target_gpu,written_cpu,written_gpu,measured_cpu,measured_gpu,cpu_c,gpu_c", csv);
            Assert.Equal(1 + r.Samples.Count, csv.Split('\n').Count(l => l.Trim().Length > 0));
            Assert.Contains("1,1,20,23,20,23,20,23,45,parked", csv);
            string report = FanCheckRunner.Report(r);
            Assert.Contains("handed back", report);
            Assert.Contains(FanCheckRunner.HandBackWarning, report);
        }

        // A run with the hold: the result, the index in Bios.Calls of the floor write (-1:
        // none), the hold's seconds so far (the callbacks see them) and the progress lines
        private sealed class HoldRun {
            public FanCheckResult Result = null!;
            public int HoldStart = -1;
            public int Second = -1;   // -1: not holding yet
            public List<string> Lines = new List<string>();
        }

        private static FanCheckResult RunWithHold(TestRig rig, HoldRun run, FanCheckHold hold,
            Action<int>? eachHoldSecond = null, CancellationToken? token = null) {
            run.Result = FanCheckRunner.Run(rig.Platform, s => {
                rig.Advance(s);
                if(run.Second >= 0) {
                    run.Second++;
                    eachHoldSecond?.Invoke(run.Second);
                }
            }, line => {
                run.Lines.Add(line);
                if(line.StartsWith("Fan check done")) {
                    run.Second = 0;
                    run.HoldStart = rig.Bios.Calls.FindLastIndex(c => c.Type == 0x2E);
                }
            }, token ?? TestContext.Current.CancellationToken, hold);
            return run.Result;
        }

        // What the hold sent: 0x2E only at the floor (or the guard's raise), besides that
        // 0x10 and reads; never 0x1A, 0x27, 0/0 or 255, nothing to the EC
        private static void AssertHoldWrites(TestRig rig, HoldRun run, params string[] levels) {
            Assert.True(run.HoldStart >= 0, "no hold");
            var calls = rig.Bios.Calls.Skip(run.HoldStart).ToList();
            Assert.Equal(levels, calls.Where(c => c.Type == 0x2E).Select(c => c.Data[0] + "/" + c.Data[1]));
            Assert.DoesNotContain(calls, c => c.Type == 0x1A || c.Type == 0x27);
            AssertNeverOffOr255(rig);
            Assert.Empty(rig.Ec.Writes);
        }

        // A normal run holds the floor with the 0x10 keep-alive, and when the OmenMon GUI
        // starts it ends without another call: no hand-back, the GUI has the fans
        [Fact]
        public void Hold_FloorAndKeepAlive_EndsSilentlyWhenTheGuiStarts() {
            using TestRig rig = Rig();
            rig.Bios.Calls.Clear();
            var run = new HoldRun();
            int callsAtGui = -1;
            FanCheckResult r = RunWithHold(rig, run, new FanCheckHold {
                GuiStarted = () => {
                    if(run.Second < 95)
                        return false;
                    callsAtGui = rig.Bios.Calls.Count;
                    return true;
                } });

            Assert.False(r.Aborted);
            Assert.False(r.HandedBack);
            Assert.Equal(FanCheckHoldEnd.GuiStarted, r.HoldEnd);
            Assert.Equal(95, r.HoldSeconds);
            Assert.Equal(new byte[] { 20, 22 }, r.HoldLevel);
            Assert.Equal(5, r.Steps.Count);
            Assert.Equal(5 * FanCheck.StepSeconds, r.Samples.Count);   // the CSV: the steps only
            Assert.Contains("Fan check done. Holding the floor level 20/22 until OmenMon starts (start it now)"
                + " or Ctrl+C / any key / 10 min to hand back to the BIOS.", run.Lines);

            AssertHoldWrites(rig, run, "20/22");
            Assert.True(rig.Bios.Calls.Skip(run.HoldStart).Count(c => c.Type == 0x10) >= 95 / FanCheck.KeepAliveSeconds);
            Assert.Equal(callsAtGui, rig.Bios.Calls.Count);   // nothing after the GUI started
            Assert.True(rig.Platform.Fans.Guard!.HoldsLevel);

            string report = FanCheckRunner.Report(r);
            Assert.Contains("Hold: 20/22 for 95 s, ended: OmenMon started", report);
            Assert.Contains("Control left to OmenMon", report);
            Assert.DoesNotContain(FanCheckRunner.HandBackWarning, report);
        }

        // The GUI already running when the steps end: no floor, no hand-back
        [Fact]
        public void Hold_GuiAlreadyRunning_WritesNothingMore() {
            using TestRig rig = Rig();
            rig.Bios.Calls.Clear();
            var run = new HoldRun();
            FanCheckResult r = RunWithHold(rig, run, new FanCheckHold { GuiStarted = () => true });
            Assert.Equal(FanCheckHoldEnd.GuiStarted, r.HoldEnd);
            Assert.False(r.HandedBack);
            Assert.Equal(-1, run.HoldStart);
            Assert.Equal(new[] { "20/23", "30/33", "40/43", "50/53", "60/63" },
                rig.Bios.LevelWrites.Select(l => l[0] + "/" + l[1]));
        }

        // Ctrl+C, a key or the time up: the hand-back (the floor as the last level) and
        // the warning that the BIOS curve may stop the fans
        [Theory]
        [InlineData(FanCheckHoldEnd.CtrlC, "Ctrl+C")]
        [InlineData(FanCheckHoldEnd.Key, "a key")]
        [InlineData(FanCheckHoldEnd.Timeout, "time up")]
        public void Hold_HandsBackWithTheWarning(FanCheckHoldEnd end, string text) {
            using TestRig rig = Rig();
            rig.Bios.Calls.Clear();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var run = new HoldRun();
            int keyCalls = 0;
            var hold = new FanCheckHold {
                Seconds = end == FanCheckHoldEnd.Timeout ? 45 : FanCheck.HoldDefaultSeconds,
                GuiStarted = () => false,
                // The first call drops a key pressed during the steps: it does not end the hold
                KeyPressed = () => ++keyCalls == 1 || (end == FanCheckHoldEnd.Key && run.Second >= 45) };
            FanCheckResult r = RunWithHold(rig, run, hold, second => {
                if(end == FanCheckHoldEnd.CtrlC && second == 45)
                    stop.Cancel();
            }, stop.Token);

            Assert.False(r.Aborted);
            Assert.True(r.HandedBack);
            Assert.Equal(end, r.HoldEnd);
            Assert.Equal(45, r.HoldSeconds);
            AssertHoldWrites(rig, run, "20/22", "20/22");   // the floor, then the hand-back
            Assert.True(rig.Bios.Calls.Skip(run.HoldStart).Count(c => c.Type == 0x10) >= 45 / FanCheck.KeepAliveSeconds);
            Assert.False(rig.Platform.Fans.Guard!.HoldsLevel);
            Assert.False(rig.Platform.Fans.HeartbeatAllowed);

            string report = FanCheckRunner.Report(r);
            Assert.Contains("Hold: 20/22 for 45 s, ended: " + text, report);
            Assert.Contains("handed back to the BIOS", report);
            Assert.Contains(FanCheckRunner.HandBackWarning, report);
        }

        // Warm during the hold: the guard raises the level (the warm safety curve), and
        // the hand-back leaves the raised level
        [Fact]
        public void Hold_GuardRaisesWhenWarm() {
            using TestRig rig = Rig();
            rig.Platform.SafetyPolicy.WarmCurve = FanSafetyPolicy.DefaultWarmCurve();   // TestRig: flat floor only
            rig.Bios.Calls.Clear();
            var run = new HoldRun();
            FanCheckResult r = RunWithHold(rig, run, new FanCheckHold { Seconds = 60 }, second => {
                if(second == 20)
                    rig.SetCpuTemperature(65);
            });
            Assert.False(r.Aborted);
            Assert.True(r.HandedBack);
            Assert.Equal(FanCheckHoldEnd.Timeout, r.HoldEnd);
            AssertHoldWrites(rig, run, "20/22", "30/33", "30/33");
        }

        // 85 °C during the hold: the abort path (0x10, the table maximum), then the hand-back
        [Fact]
        public void Hold_AbortsAt85() {
            using TestRig rig = Rig();
            rig.Bios.Calls.Clear();
            var run = new HoldRun();
            FanCheckResult r = RunWithHold(rig, run, new FanCheckHold(), second => {
                if(second == 30)
                    rig.SetCpuTemperature(86);
            });
            Assert.True(r.Aborted);
            Assert.True(r.Raised);
            Assert.Contains("85", r.AbortReason);
            Assert.Equal(FanCheckHoldEnd.Aborted, r.HoldEnd);
            Assert.Equal(30, r.HoldSeconds);
            Assert.True(r.HandedBack);
            AssertHoldWrites(rig, run, "20/22", "60/63", "60/63");
            Assert.False(rig.Platform.Fans.Guard!.HoldsLevel);
            string report = FanCheckRunner.Report(r);
            Assert.Contains("raised to the table maximum", report);
            Assert.DoesNotContain(FanCheckRunner.HandBackWarning, report);
        }

        // At 85 °C: the fans to the table maximum through the guard, no further step,
        // then the hand-back (which leaves the table maximum as the last level)
        [Fact]
        public void Run_AbortsAt85_RaisesThenHandsBack() {
            using TestRig rig = Rig();
            rig.Bios.Calls.Clear();
            int seconds = 0;

            FanCheckResult r = FanCheckRunner.Run(rig.Platform, s => {
                rig.Advance(s);
                if(++seconds == FanCheck.StepSeconds + 5)
                    rig.SetCpuTemperature(86);   // in step 2
            }, null, TestContext.Current.CancellationToken);

            Assert.True(r.Aborted);
            Assert.True(r.Raised);
            Assert.Contains("85", r.AbortReason);
            Assert.True(r.HandedBack);
            Assert.Equal(2, r.Steps.Count);
            Assert.Equal(new[] { "20/23", "30/33", "60/63", "60/63" },
                rig.Bios.LevelWrites.Select(l => l[0] + "/" + l[1]));
            Assert.False(rig.Platform.Fans.Guard!.HoldsLevel);
            AssertNeverOffOr255(rig);
            Assert.Empty(rig.Ec.Writes);
        }

        // When the temperature stops reading the check stops the same way
        [Fact]
        public void Run_AbortsWhenTheTemperatureStopsReading() {
            using TestRig rig = Rig();
            int seconds = 0;
            FanCheckResult r = FanCheckRunner.Run(rig.Platform, s => {
                rig.Advance(s);
                if(++seconds == 3) {
                    rig.Ec.LockFails = true;
                    rig.Bios.FailTypes.Add(0x23);
                }
            }, null, TestContext.Current.CancellationToken);
            Assert.True(r.Aborted);
            Assert.Contains("stopped reading", r.AbortReason);
            Assert.True(r.HandedBack);
            Assert.Equal(new byte[] { 60, 63 }, rig.Bios.LevelWrites[rig.Bios.LevelWrites.Count - 2]);
            AssertNeverOffOr255(rig);
        }

        // Ctrl+C: stops and hands back, without raising the fans
        [Fact]
        public void Run_Cancelled_HandsBack() {
            using TestRig rig = Rig();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            int seconds = 0;
            FanCheckResult r = FanCheckRunner.Run(rig.Platform, s => {
                rig.Advance(s);
                if(++seconds == 5) stop.Cancel();
            }, null, stop.Token);
            Assert.True(r.Aborted);
            Assert.False(r.Raised);
            Assert.True(r.HandedBack);
            Assert.Equal(new[] { "20/23", "20/23" }, rig.Bios.LevelWrites.Select(l => l[0] + "/" + l[1]));
            Assert.False(rig.Platform.Fans.Guard!.HoldsLevel);
        }

        // Refusals, before anything is written: an EC board (it keeps the wizard), an
        // unreadable fan table, too hot at the start
        [Fact]
        public void Refusals_WriteNothing() {
            using(var ec = new TestRig()) {
                ec.Bios.Calls.Clear();
                FanCheckResult r = FanCheckRunner.Run(ec.Platform, s => ec.Advance(s), null, TestContext.Current.CancellationToken);
                Assert.Contains("calibration wizard", r.Refusal);
                Assert.Empty(ec.Bios.Calls);
                Assert.Empty(ec.Ec.Writes);
            }
            using(TestRig rig = Rig()) {
                rig.Bios.FailTypes.Add(0x2F);
                FanCheckResult r = FanCheckRunner.Run(rig.Platform, s => rig.Advance(s), null, TestContext.Current.CancellationToken);
                Assert.Contains("0x2F", r.Refusal);
                Assert.Equal(0, rig.Bios.Count(0x2E));
            }
            using(TestRig rig = Rig()) {
                rig.SetCpuTemperature(90);
                FanCheckResult r = FanCheckRunner.Run(rig.Platform, s => rig.Advance(s), null, TestContext.Current.CancellationToken);
                Assert.Contains("Too hot", r.Refusal);
                Assert.Equal(0, rig.Bios.Count(0x2E));
                Assert.False(r.HandedBack);
            }
        }

    }

}
