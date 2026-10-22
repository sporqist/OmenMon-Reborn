  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Core;
using OmenMon.Hardware.Platform;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // R2: the rate a BIOS-only board shows is the 0x2D level against that fan's maximum
    // in the BIOS fan table (0x2F, the top row: 60/63 on 8BAC and 8C77), read once and
    // cached. No EC register (8C77's EC 0x14 FWPM keeps the last 0x2E level after it
    // expired, so it is no rate).
    [Collection(HardwareStateCollection.Name)]
    public class BiosOnlyRateTests {

        // The fan table of 8BAC and 8C77: top row 60/63
        private static readonly byte[] Table8Bac = { 2, 3, 20, 23, 40, 40, 43, 60, 60, 63, 80 };

        [Fact]
        public void RatePercent_IsTheLevelAgainstTheTableTop() {
            Assert.Equal(100, BiosFanControl.RatePercent(60, 60));
            Assert.Equal(100, BiosFanControl.RatePercent(63, 63));
            Assert.Equal(50, BiosFanControl.RatePercent(30, 60));
            Assert.Equal(32, BiosFanControl.RatePercent(20, 63));   // 31.7
            Assert.Equal(0, BiosFanControl.RatePercent(0, 60));
            Assert.Equal(100, BiosFanControl.RatePercent(70, 60));   // never above 100
            Assert.Equal(-1, BiosFanControl.RatePercent(-1, 60));
            Assert.Equal(-1, BiosFanControl.RatePercent(30, 0));     // no table
            Assert.Equal(-1, BiosFanControl.RatePercent(30, 101));   // not a sane row
        }

        // On the board: per fan against its own maximum, the table read once, nothing
        // read from or written to the EC
        [Fact]
        public void BiosOnly_RateFromLevelAndTable_NoEc() {
            using var rig = new TestRig(preset: BiosOnlySessionTests.Board8C77());
            rig.Bios.FanTable = Table8Bac;
            rig.SetCpuTemperature(45);
            rig.Platform.UpdateTemperature();
            IFanArray fans = rig.Platform.Fans;
            fans.SetLevels(new byte[] { 30, 33 });
            rig.Ec.Reads.Clear();

            Assert.Equal(50, fans.Fan[0].GetRate());
            Assert.Equal(52, fans.Fan[1].GetRate());   // 52.4
            for(int i = 0; i < 5; i++)
                fans.Fan[0].GetRate();
            Assert.Equal(1, rig.Bios.Count(0x2F));
            Assert.Empty(rig.Ec.Reads);
            Assert.Empty(rig.Ec.Writes);
            Assert.Equal(new byte[] { 60, 63 }, fans.GetTableMaximum());
            Assert.Equal(new byte[] { 60, 63 }, fans.GetPanicLevel());   // the same cache
        }

        // While the table cannot be read the rate is unknown ("--"), asked again at most
        // once a minute; the panic level keeps its fallback
        [Fact]
        public void BiosOnly_TableUnreadable_RateUnknown_RetriedOncePerMinute() {
            using var rig = new TestRig(preset: BiosOnlySessionTests.Board8C77());
            rig.Bios.FanTable = Table8Bac;
            rig.Bios.FailTypes.Add(0x2F);
            IFanArray fans = rig.Platform.Fans;

            Assert.Equal(-1, fans.Fan[0].GetRate());
            Assert.Equal(-1, fans.Fan[1].GetRate());
            Assert.Equal(1, rig.Bios.Count(0x2F));
            rig.Advance(30);
            Assert.Equal(-1, fans.Fan[0].GetRate());
            Assert.Equal(1, rig.Bios.Count(0x2F));

            rig.Bios.FailTypes.Remove(0x2F);
            rig.Advance(31);
            Assert.Equal(BiosFanControl.RatePercent(rig.Bios.MeasuredLevels[0], 60), fans.Fan[0].GetRate());
            Assert.Equal(2, rig.Bios.Count(0x2F));
        }

        // An EC model's rate is still its rate register (unchanged)
        [Fact]
        public void EcModel_RateStillFromItsRegister() {
            using var rig = new TestRig(preset: PlatformPreset.Default);
            rig.Ec.Data[PlatformPreset.Default.FanRateReadReg0] = 42;
            Assert.Equal(42, rig.Platform.Fans.Fan[0].GetRate());
            Assert.Equal(0, rig.Bios.Count(0x2F));
        }

    }

}
