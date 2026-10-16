  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // Failed or implausible temperature reads must be "no reading", never 0 °C, and the
    // fan program must treat an unknown temperature as unsafe.
    [Collection(HardwareStateCollection.Name)]
    public class SensorValidityTests {

        private const string Program = "SensorValidityTest";

        // Lowest row is 0/0 (fans off), so selecting it on a failed read is the bug
        private static void AddCurve() {
            TestRig.AddProgram(Program,
                (0, 0, 0),
                (50, 30, 30),
                (70, 45, 45),
                (85, 55, 55));
        }

        private static FanProgram StartProgram(TestRig rig) {
            AddCurve();
            var program = new FanProgram(rig.Platform, (severity, message) => { });
            Assert.True(program.Run(Program));
            return program;
        }

        [Fact]
        public void EcLockTimeout_IsNoReading() {
            var rig = new TestRig();
            rig.Ec.LockFails = true;
            IPlatformReadComponent cput = rig.Platform.Temperature[0];
            cput.Update();
            Assert.False(cput.GetReading().HasValue);
        }

        [Fact]
        public void ZeroDegrees_IsNoReading() {
            var rig = new TestRig();
            rig.SetCpuTemperature(0);
            Assert.False(rig.Platform.GetCpuReading(true).HasValue);
        }

        [Fact]
        public void FailedBiosCall_IsNoReading() {
            var rig = new TestRig();
            rig.Bios.FailAll = true;
            IPlatformReadComponent bios = rig.Platform.Temperature[2];
            bios.Update();
            Assert.False(bios.GetReading().HasValue);
        }

        [Fact]
        public void LastValidReading_SurvivesFailures_UntilItIsStale() {
            var rig = new TestRig();
            rig.SetCpuTemperature(64);
            Assert.Equal(64, rig.Platform.GetCpuReading(true).Value);

            rig.Ec.LockFails = true;
            rig.Bios.FailAll = true;
            rig.Advance(Config.TemperatureMaxAge);
            Assert.Equal(64, rig.Platform.GetCpuReading(true).Value);

            rig.Advance(1);
            Assert.False(rig.Platform.GetCpuReading(true).HasValue);
        }

        [Fact]
        public void HottestReading_IsNone_WithoutCpuReading_EvenIfGpuReads() {
            var rig = new TestRig();
            rig.SetCpuTemperature(0);
            rig.Ec.GpuTemperature = 70;
            Assert.False(rig.Platform.GetHottestReading(true).HasValue);
        }

        [Fact]
        public void Program_FailedReadsNeverSelectTheIdleRow() {
            var rig = new TestRig();
            rig.SetCpuTemperature(60);
            rig.Ec.GpuTemperature = 55;
            var program = StartProgram(rig);
            Assert.Equal(new byte[] { 30, 30 }, rig.Bios.LevelWrites[^1]);

            // Every read fails from now on
            rig.Ec.LockFails = true;
            rig.Bios.FailAll = false;
            rig.SetCpuTemperature(0); // the BIOS sensor now reads 0 as well
            for(int t = 0; t < 120; t += 15) {
                rig.Advance(15);
                program.Update();
                byte[] sent = rig.Bios.LevelWrites[^1];
                Assert.True(sent[0] >= 30 && sent[1] >= 30, $"t={t}: {sent[0]}/{sent[1]}");
            }
        }

        [Fact]
        public void Program_StaleCpuReading_SelectsTopRow() {
            var rig = new TestRig();
            rig.SetCpuTemperature(40);
            var program = StartProgram(rig);
            Assert.Equal(new byte[] { 0, 0 }, rig.Bios.LevelWrites[^1]);

            rig.Ec.LockFails = true;
            rig.SetCpuTemperature(0);
            rig.Advance(Config.TemperatureMaxAge + 1);
            program.Update();
            Assert.Equal(new byte[] { 55, 55 }, rig.Bios.LevelWrites[^1]);
        }

        [Fact]
        public void Program_SingleFailedRead_KeepsTheCurrentRow() {
            var rig = new TestRig();
            rig.SetCpuTemperature(72);
            rig.Ec.GpuTemperature = 72;
            var program = StartProgram(rig);
            Assert.Equal(new byte[] { 45, 45 }, rig.Bios.LevelWrites[^1]);

            rig.Ec.LockFails = true;
            rig.Bios.FailTypes.Add(0x23);
            rig.Advance(15);
            program.Update();
            Assert.Equal(new byte[] { 45, 45 }, rig.Bios.LevelWrites[^1]);
        }

        [Fact]
        public void Program_ParkedGpu_GpuFanFollowsCpu() {
            var rig = new TestRig();
            rig.SetCpuTemperature(88);
            rig.Ec.GpuTemperature = 50;
            var program = StartProgram(rig);
            Assert.Equal(new byte[] { 55, 30 }, rig.Bios.LevelWrites[^1]);

            // dGPU parks: GPTM reads 0
            rig.Ec.GpuTemperature = 0;
            rig.Advance(Config.TemperatureMaxAge + 1);
            program.Update();
            Assert.Equal(new byte[] { 55, 55 }, rig.Bios.LevelWrites[^1]);
        }

    }

}
