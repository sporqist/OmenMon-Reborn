  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;
using Xunit;

namespace OmenMon.Tests {

    // UI performance presets are separate from BIOS fan modes. This used to assert the
    // opposite (Eco == Quiet == LegacyQuiet == 3 in BiosData.FanMode, Custom == 0x63),
    // which is exactly what made choosing Quiet run the Eco preset and sent 0x63 to the
    // BIOS as a mode.
    [Collection(HardwareStateCollection.Name)]
    public class PerformanceModeTests {

        [Fact]
        public void BiosFanMode_HasOnlyBiosValues() {
            Assert.False(Enum.IsDefined(typeof(BiosData.FanMode), (byte) 0x63));
            Assert.DoesNotContain("Eco", Enum.GetNames(typeof(BiosData.FanMode)));
            Assert.DoesNotContain("Custom", Enum.GetNames(typeof(BiosData.FanMode)));
            Assert.Equal((byte) 3, (byte) BiosData.FanMode.LegacyQuiet);
            Assert.Equal((byte) 48, (byte) BiosData.FanMode.Default);
            Assert.Equal((byte) 49, (byte) BiosData.FanMode.Performance);
        }

        [Fact]
        public void Presets_AreDistinct_EvenWhenTheyShareABiosMode() {
            PresetSettings eco = PerformancePresets.Resolve(PerformancePreset.Eco);
            PresetSettings quiet = PerformancePresets.Resolve(PerformancePreset.Quiet);
            Assert.Equal(eco.FanMode, quiet.FanMode);
            Assert.NotNull(eco.RefreshRate);
            Assert.Null(quiet.RefreshRate);
            Assert.NotEqual(eco.Levels, quiet.Levels);
        }

        [Theory]
        [InlineData("Eco", PerformancePreset.Eco)]
        [InlineData("Quiet", PerformancePreset.Quiet)]
        [InlineData("Default", PerformancePreset.Default)]
        [InlineData("Performance", PerformancePreset.Performance)]
        [InlineData("Custom", PerformancePreset.Custom)]
        public void PresetNames_Parse(string name, PerformancePreset expected) {
            Assert.True(PerformancePresets.TryParse(name, out PerformancePreset parsed));
            Assert.Equal(expected, parsed);
            Assert.Equal(name, PerformancePresets.GetName(expected));
        }

        [Fact]
        public void EveryPreset_ResolvesToADefinedBiosMode() {
            foreach(PerformancePreset preset in PerformancePresets.All)
                Assert.True(Enum.IsDefined(typeof(BiosData.FanMode), PerformancePresets.Resolve(preset).FanMode));
        }

        [Theory]
        [InlineData("Custom")] // what the main form used to store
        [InlineData("99")]
        [InlineData("")]
        [InlineData("NoSuchMode")]
        public void CustomPreset_WithInventedMode_FallsBackToDefault(string configured) {
            string saved = Config.CustomPresetFanMode;
            try {
                Config.CustomPresetFanMode = configured;
                Assert.Equal(BiosData.FanMode.Default, PerformancePresets.Resolve(PerformancePreset.Custom).FanMode);
            } finally {
                Config.CustomPresetFanMode = saved;
            }
        }

        [Fact]
        public void CustomPreset_UsesConfiguredBiosModeAndLevels() {
            string savedMode = Config.CustomPresetFanMode;
            byte savedCpu = Config.CustomPresetCpuLevel, savedGpu = Config.CustomPresetGpuLevel;
            try {
                Config.CustomPresetFanMode = "Performance";
                Config.CustomPresetCpuLevel = 33;
                Config.CustomPresetGpuLevel = 36;
                PresetSettings custom = PerformancePresets.Resolve(PerformancePreset.Custom);
                Assert.Equal(BiosData.FanMode.Performance, custom.FanMode);
                Assert.Equal(new byte[] { 33, 36 }, custom.Levels);
            } finally {
                Config.CustomPresetFanMode = savedMode;
                Config.CustomPresetCpuLevel = savedCpu;
                Config.CustomPresetGpuLevel = savedGpu;
            }
        }

        [Fact]
        public void TryParseFanMode_AcceptsOnlyDefinedValues() {
            Assert.True(BiosData.TryParseFanMode("Performance", out BiosData.FanMode m));
            Assert.Equal(BiosData.FanMode.Performance, m);
            Assert.True(BiosData.TryParseFanMode("Eco", out m)); // legacy configuration files
            Assert.Equal(BiosData.FanMode.LegacyQuiet, m);
            Assert.False(BiosData.TryParseFanMode("99", out _));
            Assert.False(BiosData.TryParseFanMode("Custom", out _));
        }

        [Fact]
        public void DisplayedPreset_FollowsTheLastAppliedOne() {
            Assert.Equal(PerformancePreset.Eco,
                PerformancePresets.ForMode(BiosData.FanMode.LegacyQuiet, PerformancePreset.Eco));
            Assert.Equal(PerformancePreset.Quiet,
                PerformancePresets.ForMode(BiosData.FanMode.LegacyQuiet, null));
            Assert.Equal(PerformancePreset.Quiet,
                PerformancePresets.ForMode(BiosData.FanMode.LegacyQuiet, PerformancePreset.Performance));
            Assert.Null(PerformancePresets.ForMode(BiosData.FanMode.Cool, null));
        }

    }

}
