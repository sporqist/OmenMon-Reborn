  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System.Text.RegularExpressions;
using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // The fan-safety guard as wired into FanArray / FanProgram, against the simulated
    // 8BAC: what the BIOS actually receives and what the fans then do.
    [Collection(HardwareStateCollection.Name)]
    public class FanSafetyIntegrationTests {

        private const string Program = "FanSafetyTest";

        // Sensors read now (the monitor normally does this before supervising)
        private static void Sample(TestRig rig) {
            rig.Platform.UpdateTemperature();
        }

        // Arms 0x2E the way the heartbeat does
        private static void Heartbeat(TestRig rig) {
            rig.BiosCtl.GetFanCount();
        }

        [Fact]
        public void FanOff_WhileWarm_SendsFloorNotZero() {
            var rig = new TestRig();
            rig.SetCpuTemperature(65);
            Sample(rig);
            Heartbeat(rig);
            rig.Fans.SetOff(true);
            Assert.DoesNotContain(rig.Bios.LevelWrites, w => w[0] == 0 && w[1] == 0);
            Assert.Equal(new byte[] { 20, 22 }, rig.Bios.MeasuredLevels);
        }

        [Fact]
        public void FanOff_WhileCool_StopsFans_ThenFloorWhenItWarms() {
            var rig = new TestRig();
            rig.SetCpuTemperature(42);
            Sample(rig);
            Heartbeat(rig);
            rig.Fans.SetOff(true);
            Assert.True(rig.Bios.FansOff);

            rig.Advance(30);
            rig.SetCpuTemperature(56);
            Sample(rig);
            Assert.True(rig.Fans.Supervise());
            Assert.Equal(new byte[] { 20, 22 }, rig.Bios.MeasuredLevels);
        }

        [Fact]
        public void FanOff_TimeLimit_RaisesToFloor() {
            var rig = new TestRig();
            rig.SetCpuTemperature(40);
            Sample(rig);
            Heartbeat(rig);
            rig.Fans.SetOff(true);
            for(int t = 0; t < 15 * 60; t += 30) {
                rig.Advance(30);
                Heartbeat(rig);
                Sample(rig);
                rig.Fans.Supervise();
            }
            Assert.Equal(new byte[] { 20, 22 }, rig.Bios.MeasuredLevels);
        }

        [Fact]
        public void ConstantMinimum_OneFanZero_IsFlooredWhileWarm() {
            var rig = new TestRig();
            rig.SetCpuTemperature(70);
            Sample(rig);
            Heartbeat(rig);
            rig.Fans.SetLevels(new byte[] { 0, 35 });
            Assert.Equal(new byte[] { 20, 35 }, rig.Bios.LevelWrites[^1]);
        }

        [Fact]
        public void Supervise_WorksWithoutHeartbeat_ByArmingItself() {
            var rig = new TestRig();
            rig.SetCpuTemperature(40);
            Sample(rig);
            Heartbeat(rig);
            rig.Fans.SetOff(true);
            // Heartbeat paused (battery) from here on
            rig.Advance(60);
            rig.SetCpuTemperature(75);
            Sample(rig);
            rig.Fans.Supervise();
            Assert.Equal(new byte[] { 20, 22 }, rig.Bios.MeasuredLevels);
        }

        [Fact]
        public void Release_WritesSafeLevelThenSilence_AndBiosCurveResumes() {
            var rig = new TestRig();
            rig.SetCpuTemperature(40);
            Sample(rig);
            Heartbeat(rig);
            rig.Fans.SetOff(true);
            Assert.True(rig.Bios.FansOff);

            rig.Advance(10);
            rig.Fans.Release();
            var writes = rig.Bios.LevelWrites;
            Assert.Equal(new byte[] { 20, 22 }, writes[^1]);
            Assert.DoesNotContain(rig.Bios.LevelWrites, w => w[0] == 0xFF && w[1] == 0xFF);
            Assert.Equal(new byte[] { 20, 22 }, rig.Bios.MeasuredLevels);
            Assert.False(rig.Fans.HeartbeatAllowed);

            // No refresh from OmenMon: the level expires and the BIOS curve resumes
            rig.Advance(124);
            Assert.Equal(rig.Bios.CurveLevels, rig.Bios.MeasuredLevels);
            Assert.False(rig.Fans.HeartbeatAllowed);
            rig.Advance(26);
            Assert.True(rig.Fans.HeartbeatAllowed);
        }

        [Fact]
        public void ReleaseIfHeld_DoesNothing_InAutomaticMode() {
            var rig = new TestRig();
            rig.Fans.ReleaseIfHeld();
            Assert.Empty(rig.Bios.LevelWrites);
        }

        [Fact]
        public void LegacyFfFfRequest_IsAHandBack() {
            var rig = new TestRig();
            rig.SetCpuTemperature(60);
            Sample(rig);
            Heartbeat(rig);
            rig.Fans.SetLevels(new byte[] { 40, 40 });
            rig.Fans.SetLevels(new byte[] { 0xFF, 0xFF });
            Assert.Equal(new byte[] { 40, 40 }, rig.Bios.LevelWrites[^1]);
            Assert.DoesNotContain(rig.Bios.LevelWrites, w => w[0] == 0xFF && w[1] == 0xFF);
            Assert.False(rig.Fans.HeartbeatAllowed);
        }

        [Fact]
        public void ProgramTerminate_HandsBack_NeverZero() {
            var rig = new TestRig();
            TestRig.AddProgram(Program, (0, 0, 0), (60, 35, 35), (80, 50, 50));
            rig.SetCpuTemperature(40);
            Heartbeat(rig);
            var program = new FanProgram(rig.Platform, (s, m) => { });
            program.Run(Program);
            Assert.True(rig.Bios.FansOff);

            program.Terminate();
            Assert.Equal(new byte[] { 20, 22 }, rig.Bios.LevelWrites[^1]);
            Assert.DoesNotContain(rig.Bios.LevelWrites, w => w[0] == 0xFF && w[1] == 0xFF);
            Assert.False(rig.Bios.FansOff);
        }

        [Fact]
        public void ProgramSuspend_HandsBack() {
            var rig = new TestRig();
            TestRig.AddProgram(Program, (0, 0, 0), (60, 35, 35), (80, 50, 50));
            rig.SetCpuTemperature(70);
            Heartbeat(rig);
            var program = new FanProgram(rig.Platform, (s, m) => { });
            program.Run(Program);
            Assert.True(program.Suspend());
            Assert.Equal(new byte[] { 35, 35 }, rig.Bios.LevelWrites[^1]);
            Assert.DoesNotContain(rig.Bios.LevelWrites, w => w[0] == 0xFF && w[1] == 0xFF);
            Assert.False(rig.Fans.HeartbeatAllowed);
        }

        [Fact]
        public void Program_ZeroRow_IsFlooredWhenWarm() {
            var rig = new TestRig();
            // A curve whose rows below 60 °C are 0/0, at 55 °C: warm, so floored
            TestRig.AddProgram(Program, (0, 0, 0), (60, 35, 35));
            rig.SetCpuTemperature(55);
            Heartbeat(rig);
            var program = new FanProgram(rig.Platform, (s, m) => { });
            program.Run(Program);
            Assert.Equal(new byte[] { 20, 22 }, rig.Bios.LevelWrites[^1]);
        }

        [Fact]
        public void Panic_FromAutomatic_RaisesToTableMaximum_AndClearHandsBack() {
            var rig = new TestRig();
            rig.SetCpuTemperature(97);
            Sample(rig);
            rig.Fans.SetPanic(true);
            byte[] panic = rig.Fans.GetPanicLevel();
            Assert.Equal(new byte[] { 52, 55 }, panic); // the table maximum
            Assert.Equal(panic, rig.Bios.MeasuredLevels); // armed itself, no heartbeat needed

            // Re-sent on every supervised pass
            int before = rig.Bios.LevelWrites.Count;
            rig.Advance(3);
            Sample(rig);
            Assert.True(rig.Fans.Supervise());
            Assert.Equal(before + 1, rig.Bios.LevelWrites.Count);

            rig.Fans.SetPanic(false);
            Assert.Equal(panic, rig.Bios.LevelWrites[^1]); // the held level is left in effect
            Assert.DoesNotContain(rig.Bios.LevelWrites, w => w[0] == 0xFF && w[1] == 0xFF);
            Assert.False(rig.Fans.HeartbeatAllowed);
        }

        [Fact]
        public void Panic_DuringProgram_OverridesRows_AndClearRestoresThem() {
            var rig = new TestRig();
            TestRig.AddProgram(Program, (0, 25, 25), (60, 35, 35), (80, 50, 50));
            rig.SetCpuTemperature(70);
            Heartbeat(rig);
            var program = new FanProgram(rig.Platform, (s, m) => { });
            program.Run(Program);
            Assert.Equal(new byte[] { 35, 35 }, rig.Bios.LevelWrites[^1]);

            rig.Fans.SetPanic(true);
            rig.Advance(15);
            program.Update();
            Assert.Equal(new byte[] { 52, 55 }, rig.Bios.LevelWrites[^1]);

            rig.Fans.SetPanic(false);
            Assert.Equal(new byte[] { 35, 35 }, rig.Bios.LevelWrites[^1]);
        }

        [Fact]
        public void FanTableMaximum_AboveFanLevelMax_IsUsed() {
            var rig = new TestRig();
            int saved = Config.FanLevelMax;
            try {
                Config.FanLevelMax = 50;
                Assert.Equal(new byte[] { 52, 55 }, rig.Fans.GetPanicLevel());
            } finally {
                Config.FanLevelMax = saved;
            }
        }

        // fansafety-1: the table is what the firmware runs at its top; FanLevelMax (up
        // to 100) must not push panic above it
        [Fact]
        public void FanLevelMax_DoesNotPushPanicAboveTheTable() {
            var rig = new TestRig();
            int saved = Config.FanLevelMax;
            try {
                Config.FanLevelMax = 100;
                Assert.Equal(new byte[] { 52, 55 }, rig.Fans.GetPanicLevel());
            } finally {
                Config.FanLevelMax = saved;
            }
        }

        // fansafety-1: a failed 0x2F read gives the fallback (at least the warm curve's
        // top step) and is not cached; the next panic reads the table again
        [Fact]
        public void FailedFanTableRead_FallsBackSafely_AndIsRetried() {
            var rig = new TestRig();
            rig.Platform.SafetyPolicy.WarmCurve = FanSafetyPolicy.DefaultWarmCurve();
            rig.SetCpuTemperature(97);
            Sample(rig);

            rig.Bios.FailTypes.Add(0x2F);
            Assert.Equal(new byte[] { 60, 63 }, rig.Fans.GetPanicLevel());
            int reads = rig.Bios.Count(0x2F);
            rig.Fans.SetPanic(true);
            Assert.Equal(reads + 1, rig.Bios.Count(0x2F)); // not cached: read again
            Assert.Equal(new byte[] { 60, 63 }, rig.Bios.MeasuredLevels);
            rig.Fans.SetPanic(false);

            // A program row in panic gets the fallback too (program writes do not get
            // the warm curve, so this is the level that counts for them)
            Assert.Equal(new byte[] { 60, 63 }, ProgramRowInPanic(rig));

            rig.Bios.FailTypes.Remove(0x2F);
            Assert.Equal(new byte[] { 52, 55 }, rig.Fans.GetPanicLevel());

            // Cached once read: a later failure does not matter
            rig.Bios.FailTypes.Add(0x2F);
            reads = rig.Bios.Count(0x2F);
            Assert.Equal(new byte[] { 52, 55 }, rig.Fans.GetPanicLevel());
            rig.Fans.SetPanic(true);
            Assert.Equal(reads, rig.Bios.Count(0x2F));
        }

        // A fan table read that has sane rows (1-100) for one fan only: that fan gets the
        // table maximum, the other the fallback (with the warm curve: its top step, 60/63).
        // Not cached, so the next call reads 0x2F again; once the table is good for both
        // fans, its full maximum is cached.
        [Theory]
        [InlineData(false, 52, 63)] // every GPU-fan row is 0 or above 100
        [InlineData(true, 60, 55)]  // every CPU-fan row is
        public void FanTable_SaneForOneFanOnly_IsMixedWithTheFallback_AndRetried(bool cpuRowsBad, byte expectCpu, byte expectGpu) {
            var rig = new TestRig();
            rig.Platform.SafetyPolicy.WarmCurve = FanSafetyPolicy.DefaultWarmCurve();
            byte[] good = rig.Bios.FanTable;
            byte[] bad = (byte[]) good.Clone();
            byte[] garbage = { 0, 101, 255 };
            for(int row = 0; row < 3; row++)
                bad[2 + 3 * row + (cpuRowsBad ? 0 : 1)] = garbage[row];
            rig.Bios.FanTable = bad;

            Assert.Equal(new byte[] { expectCpu, expectGpu }, rig.Fans.GetPanicLevel());
            int reads = rig.Bios.Count(0x2F);
            Assert.Equal(new byte[] { expectCpu, expectGpu }, rig.Fans.GetPanicLevel());
            Assert.Equal(reads + 1, rig.Bios.Count(0x2F)); // not cached: read again

            rig.Bios.FanTable = good;
            Assert.Equal(new byte[] { 52, 55 }, rig.Fans.GetPanicLevel());
            reads = rig.Bios.Count(0x2F);
            rig.Bios.FanTable = bad;
            Assert.Equal(new byte[] { 52, 55 }, rig.Fans.GetPanicLevel());
            Assert.Equal(reads, rig.Bios.Count(0x2F)); // cached
        }

        // What a program row of 30/30 is raised to while in panic with the panic level
        // GetPanicLevel() gives now
        private static byte[] ProgramRowInPanic(TestRig rig) {
            rig.Fans.Guard.SetPanic(rig.Fans.GetPanicLevel());
            try {
                return rig.Fans.Guard.Filter(new byte[] { 30, 30 }, FanLevelSource.Program);
            } finally {
                rig.Fans.Guard.SetPanic(null);
            }
        }

        [Fact]
        public void FailedFanTableRead_WithoutWarmCurve_FallsBackToFanLevelMax() {
            var rig = new TestRig(); // warm curve "none": only the floor
            rig.Bios.FailAll = true;
            Assert.Equal(new byte[] { 55, 55 }, rig.Fans.GetPanicLevel());
        }

        // Source tripwire: the only places that issue the raw 0x2E fan-level call are
        // FanArray (behind the guard) and the CLI FanLevel command (which filters through
        // the guard itself). A new caller must go through FanArray.SetLevels.
        [Fact]
        public void RawFanLevelCalls_OnlyBehindTheGuard() {
            string root = AppContext.BaseDirectory;
            while(!File.Exists(Path.Combine(root, "OmenMon.xml")))
                root = Path.GetDirectoryName(root)!;
            var allowed = new HashSet<string> {
                Path.Combine("Hardware", "BiosCtl.cs"),
                Path.Combine("Hardware", "FanArray.cs"),
                Path.Combine("App", "Cli", "CliOp.cs"),
                Path.Combine("App", "Cli", "CliOpBios.cs") };
            // Only the application's own source folders: the checkout may also hold
            // local copies and agent worktrees (.local, .claude) with other versions
            var offenders = new[] { "App", "Driver", "External", "Hardware", "Library" }
                .Select(d => Path.Combine(root, d))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
                .Select(f => Path.GetRelativePath(root, f))
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Where(f => Regex.IsMatch(File.ReadAllText(Path.Combine(root, f)), @"\.SetFanLevel\b"))
                .Where(f => !allowed.Contains(f))
                .ToList();
            Assert.Empty(offenders);
            Assert.Matches(@"\.SetFanLevel\b", File.ReadAllText(Path.Combine(root, "Hardware", "FanArray.cs")));
            Assert.Contains("CliFanSafetyGuard().Filter",
                File.ReadAllText(Path.Combine(root, "App", "Cli", "CliOpBios.cs")));
        }

        // Source tripwire: the GUI runs thermal panic only through ThermalPanicState.Check
        // (tested in HighTemperatureTests), which takes the hottest reading together with
        // whether it is complete. The GUI itself never reads the hottest temperature or
        // feeds the state, so it cannot drop the completeness on the way.
        [Fact]
        public void ThermalPanic_GuiGoesThroughTheTestedCheck() {
            string root = AppContext.BaseDirectory;
            while(!File.Exists(Path.Combine(root, "OmenMon.xml")))
                root = Path.GetDirectoryName(root)!;
            // The code without its line comments
            Func<string, string> code = f => Regex.Replace(File.ReadAllText(f), @"//.*", "");
            Assert.Matches(@"\bop\.CheckThermalPanic\(\);",
                code(Path.Combine(root, "App", "Gui", "GuiMonitor.cs")));
            Assert.Matches(@"\bthis\.thermalPanic\.Check\(this\.Platform,",
                code(Path.Combine(root, "App", "Gui", "GuiOp.cs")));
            var offenders = Directory.GetFiles(Path.Combine(root, "App"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Where(f => Regex.IsMatch(code(f), @"\bGetHottestReading\(|\bthermalPanic\.Update\("))
                .Select(f => Path.GetRelativePath(root, f))
                .ToList();
            Assert.Empty(offenders);
        }

    }

}
