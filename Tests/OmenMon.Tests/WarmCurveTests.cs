  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Core;
using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // Warm safety curve: manual levels while warm get at least the curve for the hottest reading.
    // Found on hardware (2026-10-16): CLI FanLevel=0,0 under an 80 W GPU load was floored to 20/22
    // and the GPU went from 64 to 86 °C in 35 s.
    [Collection(HardwareStateCollection.Name)]
    public class WarmCurveTests {

        private DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        private SensorReading cpu = SensorReading.None;
        private GpuReading gpu = GpuReading.None;

        private FanSafetyGuard NewGuard() {
            return new FanSafetyGuard(new FanSafetyPolicy(), () => this.cpu, () => this.gpu, () => this.now);
        }

        // gpuC null: a GPU that has never read (no sensor)
        private void Temps(int cpuC, int? gpuC) {
            this.cpu = new SensorReading(cpuC, this.now);
            this.gpu = gpuC.HasValue ? new GpuReading(new SensorReading(gpuC.Value, this.now), default(DateTime)) : GpuReading.None;
        }

        // After a fan program is handed back, a later panic with nothing requested is a
        // manual level: the warm curve applies on top of the panic level (before, the
        // source stayed Program and only the panic level counted)
        [Fact]
        public void PanicAfterProgramHandBack_GetsTheCurve() {
            var g = NewGuard();
            Temps(65, 66);
            g.RecordWrite(new byte[] { 30, 30 }, g.Filter(new byte[] { 30, 30 }, FanLevelSource.Program), FanLevelSource.Program);
            g.RecordRelease();
            Assert.Equal(FanLevelSource.Manual, g.RequestedSource);

            Temps(88, 91);
            g.SetPanic(new byte[] { 55, 55 });
            Assert.Equal(new byte[] { 60, 63 }, g.Supervise());
        }

        // fansafety-0: the GPU read before and its reads now fail; the CPU still reads
        [Fact]
        public void Manual_UnknownGpu_GetsTheTopStep_ParkedGpuDoesNot() {
            var g = NewGuard();
            Temps(55, 55);
            Assert.Equal(new byte[] { 20, 22 }, g.Filter(new byte[] { 0, 0 }));
            this.now += TimeSpan.FromSeconds(46);
            this.cpu = new SensorReading(55, this.now);
            Assert.Equal(new byte[] { 60, 63 }, g.Filter(new byte[] { 0, 0 }));
            Assert.Equal(new byte[] { 60, 63 }, g.HandBackLevel());

            // Parked instead (a successful 0 read): judged on the CPU alone
            this.gpu = new GpuReading(this.gpu.Last, this.now);
            Assert.Equal(new byte[] { 20, 22 }, g.Filter(new byte[] { 0, 0 }));
        }

        [Theory]
        [InlineData(55, null, 20, 22)]   // warm, below the first step: flat floor
        [InlineData(60, null, 30, 33)]
        [InlineData(64, 72, 40, 43)]     // the hotter GPU decides
        [InlineData(72, 86, 60, 63)]     // the measured case
        [InlineData(85, null, 60, 63)]
        public void Manual_OffWhileWarm_GetsTheCurve(int cpuC, int? gpuC, byte expectCpu, byte expectGpu) {
            var g = NewGuard();
            Temps(cpuC, gpuC);
            Assert.Equal(new byte[] { expectCpu, expectGpu }, g.Filter(new byte[] { 0, 0 }));
        }

        [Fact]
        public void Manual_AboveTheCurve_PassesThrough() {
            var g = NewGuard();
            Temps(72, 74);
            Assert.Equal(new byte[] { 50, 55 }, g.Filter(new byte[] { 50, 55 }));
        }

        [Fact]
        public void Manual_UnknownTemperature_GetsTheTopStep() {
            var g = NewGuard();
            Assert.Equal(new byte[] { 60, 63 }, g.Filter(new byte[] { 0, 0 }));
        }

        [Fact]
        public void Program_IsNotRaisedByTheCurve_OnlyByTheFloor() {
            var g = NewGuard();
            Temps(72, 74);
            Assert.Equal(new byte[] { 38, 41 }, g.Filter(new byte[] { 38, 41 }, FanLevelSource.Program));
            Assert.Equal(new byte[] { 20, 22 }, g.Filter(new byte[] { 0, 0 }, FanLevelSource.Program));
        }

        [Fact]
        public void Cool_WaivesTheCurve() {
            var g = NewGuard();
            Temps(45, 46);
            Assert.Equal(new byte[] { 0, 0 }, g.Filter(new byte[] { 0, 0 }));
        }

        [Fact]
        public void Supervise_RaisesAHeldManualLevel_AsItWarms() {
            var g = NewGuard();
            Temps(55, null);
            byte[] w = g.Filter(new byte[] { 0, 0 });
            g.RecordWrite(new byte[] { 0, 0 }, w);
            Assert.Equal(new byte[] { 20, 22 }, w);
            Assert.True(g.NeedsSupervision);

            Temps(75, 82);
            Assert.Equal(new byte[] { 60, 63 }, g.Supervise());
        }

        [Fact]
        public void HandBack_WhileWarm_LeavesTheCurveLevel() {
            var g = NewGuard();
            Temps(55, null);
            g.RecordWrite(new byte[] { 25, 27 }, new byte[] { 25, 27 });
            Temps(71, 73);
            Assert.Equal(new byte[] { 40, 43 }, g.HandBackLevel());
        }

        [Theory]
        [InlineData("60:30/33,70:40/43,80:60/63", 3)]
        [InlineData("none", 0)]
        [InlineData("NONE", 0)]
        public void ParseList_Valid(string text, int count) {
            Assert.Equal(count, SafetyStep.ParseList(text)!.Length);
        }

        [Theory]
        [InlineData("")]
        [InlineData("70:40/43,60:30/33")]   // not ascending
        [InlineData("60:30")]
        [InlineData("60:300/33")]
        [InlineData("20:30/33")]            // below 30 C
        [InlineData("x:1/2")]
        public void ParseList_Invalid_IsNull(string text) {
            Assert.Null(SafetyStep.ParseList(text));
        }

        [Fact]
        public void CliPath_UsesTheConfiguredCurve() {
            string saved = Config.FanSafetyWarmCurve;
            try {
                Config.FanSafetyWarmCurve = "60:35/38";
                var g = new FanSafetyGuard(FanSafetyPolicy.FromConfig(), () => this.cpu, () => this.gpu, () => this.now);
                Temps(65, null);
                Assert.Equal(new byte[] { 35, 38 }, g.Filter(new byte[] { 0, 0 }));
            } finally {
                Config.FanSafetyWarmCurve = saved;
            }
        }

    }

}
