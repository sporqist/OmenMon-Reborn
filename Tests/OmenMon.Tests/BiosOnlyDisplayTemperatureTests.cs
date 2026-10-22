  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Core;
using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // R3: a model's extra temperatures (named EC registers, the ACPI names) are shown and
    // fed into nothing: not the maximum that drives the fans, not thermal panic, not the
    // programs, not the fan-safety guard. A value outside 1-120 °C is shown as invalid.
    [Collection(HardwareStateCollection.Name)]
    public class BiosOnlyDisplayTemperatureTests {

        // 8C77 with the shipped display sensors
        private static PlatformPreset Board() {
            PlatformPreset p = BiosOnlySessionTests.Board8C77();
            p.DisplaySensors.Add(new DisplaySensorEntry("CTMP", 0xB0));
            p.DisplaySensors.Add(new DisplaySensorEntry("SKTB", 0xB3));
            p.DisplaySensors.Add(new DisplaySensorEntry("EST1", 0xB6));
            p.DisplaySensors.Add(new DisplaySensorEntry("EST2", 0xB7));
            p.DisplaySensors.Add(new DisplaySensorEntry("CATT", 0xA7));
            return p;
        }

        private static int Shown(Platform platform, string name) {
            return platform.Temperature.Single(t => t.GetName() == name).GetValue();
        }

        [Fact]
        public void Rules_ValidRangeAndNames() {
            Assert.False(DisplayTemperature.IsValid(0));
            Assert.True(DisplayTemperature.IsValid(1));
            Assert.True(DisplayTemperature.IsValid(120));
            Assert.False(DisplayTemperature.IsValid(121));
            Assert.False(DisplayTemperature.IsValid(255));
            Assert.Equal(DisplayTemperature.Invalid, DisplayTemperature.Shown(true, 0));
            Assert.Equal(DisplayTemperature.Invalid, DisplayTemperature.Shown(false, 50));
            Assert.Equal(50, DisplayTemperature.Shown(true, 50));

            Assert.True(DisplayTemperature.IsValidName("SKTB"));
            Assert.False(DisplayTemperature.IsValidName("CPUT"));   // the fan logic's names
            Assert.False(DisplayTemperature.IsValidName("gptm"));
            Assert.False(DisplayTemperature.IsValidName("BIOS"));
            Assert.False(DisplayTemperature.IsValidName(""));
            Assert.False(DisplayTemperature.IsValidName("TOO-LONG-NAME"));
        }

        // Shown under their names, invalid outside 1-120 °C, and fed into nothing: the
        // maximum, the hottest reading (panic), the guard and its cool check, the CPU and
        // GPU readings all stay on CPU 45 °C with a parked GPU, while SKTB reads 110
        [Fact]
        public void ShownButFedIntoNothing() {
            using var rig = new TestRig(preset: Board());
            rig.SetCpuTemperature(45);
            rig.Ec.GpuTemperature = 0;              // parked
            rig.Ec.Data[0xB3] = 110;                // valid, hot: display only
            rig.Ec.Data[0xB0] = 0;                  // invalid
            rig.Ec.Data[0xB6] = 121;                // invalid
            rig.Ec.Data[0xA7] = 0xC8;               // invalid (200)
            rig.Ec.Data[0xB7] = 44;
            Platform platform = rig.Platform;
            platform.UpdateTemperature();

            Assert.Equal(110, Shown(platform, "SKTB"));
            Assert.Equal(44, Shown(platform, "EST2"));
            Assert.Equal(DisplayTemperature.Invalid, Shown(platform, "CTMP"));
            Assert.Equal(DisplayTemperature.Invalid, Shown(platform, "EST1"));
            Assert.Equal(DisplayTemperature.Invalid, Shown(platform, "CATT"));
            for(int i = 0; i < platform.Temperature.Length; i++)
                if(platform.Temperature[i] is DisplayTemperatureComponent) {
                    Assert.False(platform.TemperatureUse[i]);
                    Assert.False(platform.Temperature[i].GetReading().HasValue);
                }

            Assert.Equal(45, platform.GetMaxTemperature());
            Assert.Equal(45, platform.GetCpuTemperature());
            Assert.Equal(0, platform.GetGpuTemperature());
            Assert.Equal(45, platform.GetHottestReading().Value);
            Assert.Equal(45, platform.Fans.Guard!.Hottest());
            Assert.True(platform.Fans.Guard.IsCool());

            var panic = new ThermalPanicState();
            Assert.Equal(ThermalPanicTransition.None, panic.Check(platform, true, 90, 5, out _));
            Assert.False(platform.Fans.Guard.IsPanic);
            Assert.Empty(rig.Ec.Writes);
        }

        // A failed read shows invalid too, and the others keep reading
        [Fact]
        public void FailedRead_IsInvalid() {
            using var rig = new TestRig(preset: Board());
            rig.Ec.Data[0xB3] = 60;
            rig.Ec.FailingReads.Add(0xB3);
            rig.Platform.UpdateTemperature();
            Assert.Equal(DisplayTemperature.Invalid, Shown(rig.Platform, "SKTB"));
            rig.Ec.FailingReads.Clear();
            rig.Platform.UpdateTemperature();
            Assert.Equal(60, Shown(rig.Platform, "SKTB"));
        }

        // An EC model gets them too, after its configured sensors, never used for the
        // maximum (its own sensors and their use flags unchanged)
        [Fact]
        public void EcModel_DisplaySensorsAfterItsOwn() {
            PlatformPreset p = new PlatformPreset { ProductId = "EC01", FanControl = FanControlKind.Ec };
            IPlatformReadComponent[] plain = Platform.CreateTemperatures(p, out bool[] plainUse, new FakeEcRegisters());
            p.DisplaySensors.Add(new DisplaySensorEntry("SKTB", 0xB3));
            IPlatformReadComponent[] with = Platform.CreateTemperatures(p, out bool[] withUse, new FakeEcRegisters());
            Assert.Equal(plain.Length + 1, with.Length);
            Assert.Equal(plainUse, withUse.Take(plainUse.Length).ToArray());
            Assert.False(withUse.Last());
            Assert.Equal("SKTB", with.Last().GetName());
        }

    }

}
