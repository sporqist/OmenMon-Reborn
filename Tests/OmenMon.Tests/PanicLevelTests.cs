  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Core;

namespace OmenMon.Tests {

    // PanicLevel (core): the thermal-panic level from the BIOS fan table, and the fallback
    // without one. FanArray.GetPanicLevel reads the table and delegates; the fan-safety
    // integration tests cover it through the simulated board.
    public class PanicLevelTests {

        [Fact]
        public void TableMaximum_PerFan() {
            Assert.Equal(new byte[] { 52, 55 }, PanicLevel.TableMaximum(new (byte, byte)[] { (20, 22), (52, 55), (35, 37) }));
            Assert.Equal(new byte[] { 60, 63 }, PanicLevel.TableMaximum(new (byte, byte)[] { (60, 23), (20, 63) }));
        }

        // Values above 100 are not levels (0xFF rows, garbage from a short read), and 0 is
        // no level: such a fan has no table maximum
        [Fact]
        public void TableMaximum_IgnoresOutOfRange() {
            Assert.Equal(new byte[] { 100, 0 }, PanicLevel.TableMaximum(new (byte, byte)[] { (100, 101), (0xFF, 0xFF), (0, 0) }));
            Assert.Equal(new byte[] { 0, 0 }, PanicLevel.TableMaximum(new (byte, byte)[0]));
        }

        [Theory]
        [InlineData(60, 63, true)]
        [InlineData(60, 0, false)]
        [InlineData(0, 63, false)]
        [InlineData(0, 0, false)]
        public void IsComplete(byte cpu, byte gpu, bool complete) {
            Assert.Equal(complete, PanicLevel.IsComplete(new byte[] { cpu, gpu }));
        }

        // Without the table: Config.FanLevelMax clamped to 1-100, but at least the warm
        // curve's top step (60/63 by default); a fan with a table maximum keeps it
        [Theory]
        [InlineData(0, 0, 55, 60, 63)]      // the default FanLevelMax (55)
        [InlineData(0, 0, 70, 70, 70)]
        [InlineData(0, 0, 150, 100, 100)]   // clamped to 100
        [InlineData(0, 0, -5, 60, 63)]      // clamped to 1, then the curve
        [InlineData(45, 0, 55, 45, 63)]     // the CPU fan read from the table
        [InlineData(0, 48, 55, 60, 48)]
        public void Choose_DefaultCurve(byte tableCpu, byte tableGpu, int configuredMax, byte cpu, byte gpu) {
            Assert.Equal(new byte[] { cpu, gpu },
                PanicLevel.Choose(new byte[] { tableCpu, tableGpu }, configuredMax, new FanSafetyPolicy()));
        }

        // Without a warm curve the floor is the lower bound
        [Theory]
        [InlineData(55, 55, 55)]
        [InlineData(1, 20, 22)]
        public void Choose_NoCurve(int configuredMax, byte cpu, byte gpu) {
            var policy = new FanSafetyPolicy { WarmCurve = new SafetyStep[0] };
            Assert.Equal(new byte[] { cpu, gpu }, PanicLevel.Choose(new byte[2], configuredMax, policy));
        }

    }

}
