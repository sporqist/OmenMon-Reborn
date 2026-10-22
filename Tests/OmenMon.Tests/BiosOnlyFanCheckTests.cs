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
    // hand-back at the end, never 0/0 or 255, and nothing written to the EC
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
        }

        // The whole check: five steps in order through the guard, 0x10 within each step,
        // 0x2D every second, then the hand-back; no EC write, no 0/0, no 255
        [Fact]
        public void Run_StepsKeepAliveHandBack_NoEcWrite() {
            using TestRig rig = Rig();
            rig.Bios.Calls.Clear();

            FanCheckResult r = FanCheckRunner.Run(rig.Platform, s => rig.Advance(s), null, TestContext.Current.CancellationToken);

            Assert.Null(r.Refusal);
            Assert.False(r.Aborted);
            Assert.True(r.HandedBack);
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
