using System;
using System.Linq;
using OmenMon.Hardware.Bios;
using OmenMon.Tests.Fakes;
using Xunit;

namespace OmenMon.Tests {

    // The graphics mode (BIOS type 0x52, the MUX) is read only through the read command
    // and cannot be switched until the MUX gate is passed (research\mux-gate.md): on
    // 8BAC command 2 is the firmware's write dispatcher, and a wrong mode can leave the
    // internal panel dark at the next boot
    public class GpuModeGateTests {

        [Fact]
        public void GetGpuMode_ReadsWithTheReadCommandOnly() {
            Fake8BacBios bios = new Fake8BacBios();
            new BiosCtl(bios).GetGpuMode();
            Assert.Equal(new[] { BiosData.Cmd.Legacy }, bios.Commands.ToArray());
            Assert.Equal(0x52u, Assert.Single(bios.Calls).Type);
        }

        // Only if the read command fails is Cmd.GpuMode tried, and then without data
        [Fact]
        public void GetGpuMode_FallsBackWithoutData() {
            Fake8BacBios bios = new Fake8BacBios();
            bios.FailTypes.Add(0x52);
            new BiosCtl(bios).GetGpuMode();
            Assert.Equal(new[] { BiosData.Cmd.Legacy, BiosData.Cmd.GpuMode }, bios.Commands.ToArray());
            Assert.All(bios.Calls, c => Assert.All(c.Data, b => Assert.Equal(0, b)));
        }

        [Fact]
        public void SetGpuMode_IsLocked_AndSendsNothing() {
            Assert.False(BiosCtl.GpuModeWriteAllowed);
            Fake8BacBios bios = new Fake8BacBios();
            BiosCtl ctl = new BiosCtl(bios);
            foreach(BiosData.GpuMode mode in Enum.GetValues(typeof(BiosData.GpuMode)).Cast<BiosData.GpuMode>())
                Assert.Throws<InvalidOperationException>(() => ctl.SetGpuMode(mode));
            Assert.Empty(bios.Calls);
        }

    }

}
