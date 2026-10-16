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
        public void Release_WritesSafeLevelThenFfFf_AndBiosCurveResumes() {
            var rig = new TestRig();
            rig.SetCpuTemperature(40);
            Sample(rig);
            Heartbeat(rig);
            rig.Fans.SetOff(true);
            Assert.True(rig.Bios.FansOff);

            rig.Advance(10);
            rig.Fans.Release();
            var writes = rig.Bios.LevelWrites;
            Assert.Equal(new byte[] { 20, 22 }, writes[^2]);
            Assert.Equal(new byte[] { 0xFF, 0xFF }, writes[^1]);
            Assert.Equal(new byte[] { 20, 22 }, rig.Bios.MeasuredLevels);
            Assert.False(rig.Fans.HeartbeatAllowed);

            // No refresh from OmenMon: the level expires and the BIOS curve resumes
            rig.Advance(124);
            Assert.Equal(rig.Bios.CurveLevels, rig.Bios.MeasuredLevels);
            rig.Advance(6);
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
            Assert.Equal(new byte[] { 40, 40 }, rig.Bios.LevelWrites[^2]);
            Assert.Equal(new byte[] { 0xFF, 0xFF }, rig.Bios.LevelWrites[^1]);
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
            Assert.Equal(new byte[] { 20, 22 }, rig.Bios.LevelWrites[^2]);
            Assert.Equal(new byte[] { 0xFF, 0xFF }, rig.Bios.LevelWrites[^1]);
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
            Assert.Equal(new byte[] { 35, 35 }, rig.Bios.LevelWrites[^2]);
            Assert.Equal(new byte[] { 0xFF, 0xFF }, rig.Bios.LevelWrites[^1]);
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
            Assert.Equal(new byte[] { 55, 55 }, panic); // table max 52/55, at least FanLevelMax 55
            Assert.Equal(panic, rig.Bios.MeasuredLevels); // armed itself, no heartbeat needed

            // Re-sent on every supervised pass
            int before = rig.Bios.LevelWrites.Count;
            rig.Advance(3);
            Sample(rig);
            Assert.True(rig.Fans.Supervise());
            Assert.Equal(before + 1, rig.Bios.LevelWrites.Count);

            rig.Fans.SetPanic(false);
            Assert.Equal(new byte[] { 0xFF, 0xFF }, rig.Bios.LevelWrites[^1]);
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
            Assert.Equal(new byte[] { 55, 55 }, rig.Bios.LevelWrites[^1]);

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
            var offenders = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f))
                .Where(f => !f.StartsWith("Tests") && !f.StartsWith("obj") && !f.StartsWith("Bin"))
                .Where(f => Regex.IsMatch(File.ReadAllText(Path.Combine(root, f)), @"\.SetFanLevel\b"))
                .Where(f => !allowed.Contains(f))
                .ToList();
            Assert.Empty(offenders);
            Assert.Contains("CliFanSafetyGuard().Filter",
                File.ReadAllText(Path.Combine(root, "App", "Cli", "CliOpBios.cs")));
        }

    }

}
