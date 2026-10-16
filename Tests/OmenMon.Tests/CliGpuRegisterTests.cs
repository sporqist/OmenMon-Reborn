  //\   OmenMon: Hardware Monitoring & Control Utility
 //  \  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Hardware.Ec;
using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // The CLI fan guard reads the GPU temperature from the same EC register the GUI
    // uses: the model's TempGpuReg override, else the configured GPTM sensor
    [Collection(HardwareStateCollection.Name)]
    public class CliGpuRegisterTests {

        [Fact]
        public void ModelOverride_Wins() {
            Config.Models["T3ST"] = new PlatformPreset { TempGpuReg = 0xB4 };
            try {
                Assert.Equal(0xB4, Platform.GpuTemperatureRegister("T3ST"));
            } finally {
                Config.Models.Remove("T3ST");
            }
        }

        [Fact]
        public void UnknownProduct_UsesTheConfiguredSensor() {
            Assert.Equal((byte) EmbeddedControllerData.Register.GPTM, Platform.GpuTemperatureRegister("NONE"));
            Assert.Equal((byte) EmbeddedControllerData.Register.GPTM, Platform.GpuTemperatureRegister(null));
        }

    }

}
