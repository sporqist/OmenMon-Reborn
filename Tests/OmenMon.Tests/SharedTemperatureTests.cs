  //\   OmenMon: Hardware Monitoring & Control Utility
 //  \  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // FanProgramSharedTemperature: both fans follow the hotter chip (shared heat pipes)
    [Collection(HardwareStateCollection.Name)]
    public class SharedTemperatureTests {

        private const string Program = "SharedTemperatureTest";

        private static FanProgram NewProgram(TestRig rig) {
            TestRig.AddProgram(Program,
                (0, 22, 25),
                (50, 30, 33),
                (70, 45, 48),
                (85, 55, 58));
            var program = new FanProgram(rig.Platform, (severity, message) => { });
            Assert.True(program.Run(Program));
            return program;
        }

        [Theory]
        [InlineData(false, 30, 48)]  // each fan follows its own chip
        [InlineData(true, 45, 48)]   // both follow the hotter GPU
        public void GpuLoad_RaisesCpuFan_OnlyWhenShared(bool shared, byte expectCpu, byte expectGpu) {
            bool saved = Config.FanProgramSharedTemperature;
            try {
                Config.FanProgramSharedTemperature = shared;
                var rig = new TestRig();
                var program = NewProgram(rig);
                DateTime now = DateTime.UtcNow;
                byte[] levels = program.SelectLevels(new SensorReading(55, now), GpuState.Fresh, 72, out bool safe);
                Assert.False(safe);
                Assert.Equal(new byte[] { expectCpu, expectGpu }, levels);
            } finally {
                Config.FanProgramSharedTemperature = saved;
            }
        }

        // fansafety-0: an unknown GPU (failed reads) gets the top row on the GPU fan, and
        // on both fans when they share the temperature; a parked GPU follows the CPU
        [Theory]
        [InlineData(false, GpuState.Unknown, 30, 58, true)]
        [InlineData(true, GpuState.Unknown, 55, 58, true)]
        [InlineData(false, GpuState.Parked, 30, 33, false)]
        [InlineData(true, GpuState.Parked, 30, 33, false)]
        public void UnknownGpu_GetsTheTopRow(bool shared, GpuState state, byte expectCpu, byte expectGpu, bool expectSafe) {
            bool saved = Config.FanProgramSharedTemperature;
            try {
                Config.FanProgramSharedTemperature = shared;
                var rig = new TestRig();
                var program = NewProgram(rig);
                byte[] levels = program.SelectLevels(new SensorReading(55, DateTime.UtcNow), state, 0, out bool safe);
                Assert.Equal(expectSafe, safe);
                Assert.Equal(new byte[] { expectCpu, expectGpu }, levels);
            } finally {
                Config.FanProgramSharedTemperature = saved;
            }
        }

    }

}
