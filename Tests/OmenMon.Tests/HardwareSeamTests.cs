  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Platform;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // The BIOS/EC seams and the simulated 8BAC behind them. These pin the measured
    // board rules, so fan-logic tests built on the fake test against the real board.
    [Collection(HardwareStateCollection.Name)]
    public class HardwareSeamTests {

        [Fact]
        public void TestProcess_CannotOpenRealBiosOrEc() {
            Assert.True(OmenMon.Library.Hw.Isolated);
            Assert.Null(OmenMon.Library.Hw.EcInterface());
            Assert.Null(OmenMon.Library.Hw.BiosInterface());
            Assert.False(OmenMon.Library.Hw.EcExecBatch(() => throw new InvalidOperationException("must not run")));
        }

        [Fact]
        public void BiosCtl_EncodesFanLevelAsType2E_AfterFanCountQuery() {
            var rig = new TestRig();
            rig.BiosCtl.SetFanLevel(new byte[] { 30, 33 });
            Assert.Equal(2, rig.Bios.Calls.Count);
            Assert.Equal(0x10u, rig.Bios.Calls[0].Type);
            Assert.Equal(0x2Eu, rig.Bios.Calls[1].Type);
            Assert.Equal(new byte[] { 30, 33, 0, 0 }, rig.Bios.Calls[1].Data);
        }

        [Fact]
        public void FanLevel_IsIgnoredWithoutPriorFanCountQuery() {
            // The simulated BIOS itself: a raw 0x2E without a 0x10 in the last 30 s
            var rig = new TestRig();
            rig.Bios.Send(BiosData.Cmd.Default, 0x2E, new byte[] { 40, 40, 0, 0 }, 0, out _);
            Assert.Null(rig.Bios.ActiveLevels);
            Assert.Equal(rig.Bios.CurveLevels, rig.BiosCtl.GetFanLevel());
        }

        [Fact]
        public void SetFanLevel_TakesEffect_BecauseItQueriesTheFanCountFirst() {
            var rig = new TestRig();
            rig.BiosCtl.SetFanLevel(new byte[] { 40, 40 });
            Assert.Equal(new byte[] { 40, 40 }, rig.Bios.ActiveLevels);
        }

        [Fact]
        public void FanLevel_TakesEffectWithin30sOfFanCountQuery() {
            var rig = new TestRig();
            rig.BiosCtl.GetFanCount();
            rig.Bios.Advance(TimeSpan.FromSeconds(30));
            rig.BiosCtl.SetFanLevel(new byte[] { 40, 41 });
            Assert.Equal(new byte[] { 40, 41 }, rig.BiosCtl.GetFanLevel());
        }

        [Fact]
        public void FanLevel_ExpiresAbout124sAfterLastSet() {
            var rig = new TestRig();
            rig.BiosCtl.GetFanCount();
            rig.BiosCtl.SetFanLevel(new byte[] { 40, 41 });
            rig.Bios.Advance(TimeSpan.FromSeconds(123));
            Assert.Equal(new byte[] { 40, 41 }, rig.Bios.MeasuredLevels);
            rig.Bios.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(rig.Bios.CurveLevels, rig.Bios.MeasuredLevels);
        }

        [Fact]
        public void FanCountQuery_KeepsActiveLevelAlive() {
            var rig = new TestRig();
            rig.BiosCtl.GetFanCount();
            rig.BiosCtl.SetFanLevel(new byte[] { 40, 41 });
            for(int i = 0; i < 10; i++) {
                rig.Bios.Advance(TimeSpan.FromSeconds(30));
                rig.BiosCtl.GetFanCount();
            }
            Assert.Equal(new byte[] { 40, 41 }, rig.Bios.MeasuredLevels);
        }

        [Fact]
        public void ZeroZero_SwitchesFansOff_AndFfFfKeepsIt() {
            var rig = new TestRig();
            rig.BiosCtl.GetFanCount();
            rig.BiosCtl.SetFanLevel(new byte[] { 0, 0 });
            Assert.True(rig.Bios.FansOff);
            rig.BiosCtl.SetFanLevel(new byte[] { 0xFF, 0xFF });
            Assert.True(rig.Bios.FansOff);
        }

        [Fact]
        public void FanArray_SetLevels_GoesThroughInjectedBios() {
            var rig = new TestRig();
            rig.BiosCtl.GetFanCount();
            rig.Fans.SetLevels(new byte[] { 33, 34 });
            Assert.Equal(new byte[] { 33, 34 }, rig.Fans.GetLevels());
            Assert.Equal(34, rig.Fans.Fan[1].GetLevel());
        }

        [Fact]
        public void EcComponent_ReadsThroughInjectedRegisters() {
            var rig = new TestRig();
            rig.Ec.CpuTemperature = 61;
            IPlatformReadComponent cput = rig.Platform.Temperature[0];
            Assert.True(cput.Update());
            Assert.Equal(61, cput.GetValue());
        }

        [Fact]
        public void BiosTemperatureComponent_ReadsType23() {
            var rig = new TestRig();
            rig.Bios.CpuTemperature = 57;
            IPlatformReadComponent bios = rig.Platform.Temperature[2];
            Assert.True(bios.Update());
            Assert.Equal(57, bios.GetValue());
            Assert.Contains(rig.Bios.Calls, c => c.Type == 0x23);
        }

    }

}
