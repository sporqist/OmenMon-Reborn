  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using OmenMon.Hardware.Bios;
using OmenMon.Library;

namespace OmenMon.Hardware.Platform {

    // User-facing performance presets, modelled on OMEN Gaming Hub (#102). These are
    // OmenMon's own choices, not BIOS fan modes: each one resolves to a real BIOS
    // thermal policy (BiosData.FanMode) plus GPU power, optionally a display refresh
    // rate, and fixed fan levels. Several presets may share a BIOS mode (Eco and Quiet
    // both use thermal policy 3), so the preset is the thing to remember, not the mode.
    public enum PerformancePreset {
        Eco,
        Quiet,
        Default,
        Performance,
        Custom
    }

    // What applying a preset does
    public sealed class PresetSettings {

        // BIOS thermal policy to set (always a defined BiosData.FanMode value)
        public BiosData.FanMode FanMode;

        // GPU power level to set, or null to leave it
        public BiosData.GpuPowerLevel? GpuPower;

        // Display refresh rate to set [Hz], or null to leave it
        public int? RefreshRate;

        // Fixed fan levels [rpm / 100] to set afterwards (through the fan-safety guard),
        // or null to leave the fans to the BIOS thermal policy
        public byte[] Levels;

    }

    public static class PerformancePresets {

        // In menu order
        public static readonly PerformancePreset[] All = {
            PerformancePreset.Eco,
            PerformancePreset.Quiet,
            PerformancePreset.Default,
            PerformancePreset.Performance,
            PerformancePreset.Custom };

        public static string GetName(PerformancePreset preset) {
            return Enum.GetName(typeof(PerformancePreset), preset);
        }

        public static bool TryParse(string name, out PerformancePreset preset) {
            return Enum.TryParse(name, false, out preset)
                && Enum.IsDefined(typeof(PerformancePreset), preset);
        }

        // Resolves a preset to concrete settings. The fixed levels are the values the
        // presets have always used (#102); they are levels, not caps, and the fan-safety
        // guard and thermal panic apply to them like to any other level.
        public static PresetSettings Resolve(PerformancePreset preset) {
            switch(preset) {

                case PerformancePreset.Eco:
                    return new PresetSettings {
                        FanMode = BiosData.FanMode.LegacyQuiet,
                        GpuPower = BiosData.GpuPowerLevel.Min,
                        RefreshRate = Config.PresetRefreshRateLow,
                        Levels = new byte[] { 28, 28 } };

                case PerformancePreset.Quiet:
                    return new PresetSettings {
                        FanMode = BiosData.FanMode.LegacyQuiet,
                        GpuPower = BiosData.GpuPowerLevel.Min,
                        Levels = new byte[] { 32, 32 } };

                case PerformancePreset.Performance:
                    return new PresetSettings {
                        FanMode = BiosData.FanMode.Performance,
                        GpuPower = BiosData.GpuPowerLevel.Max,
                        Levels = new byte[] { 55, 55 } };

                case PerformancePreset.Custom: {
                    // The BIOS mode comes from <CustomPreset><Mode>; anything that is not
                    // a defined BIOS fan mode (e.g. "Custom", which used to be sent to the
                    // BIOS as 0x63) falls back to Default
                    BiosData.FanMode mode;
                    if(!BiosData.TryParseFanMode(Config.CustomPresetFanMode, out mode))
                        mode = BiosData.FanMode.Default;
                    BiosData.GpuPowerLevel gpu;
                    bool hasGpu = Enum.TryParse(Config.CustomPresetGpuPower, true, out gpu)
                        && Enum.IsDefined(typeof(BiosData.GpuPowerLevel), gpu);
                    return new PresetSettings {
                        FanMode = mode,
                        GpuPower = hasGpu ? gpu : (BiosData.GpuPowerLevel?) null,
                        Levels = Config.CustomPresetCpuLevel > 0 && Config.CustomPresetGpuLevel > 0
                            ? new byte[] { Config.CustomPresetCpuLevel, Config.CustomPresetGpuLevel }
                            : null };
                }

                case PerformancePreset.Default:
                default:
                    return new PresetSettings {
                        FanMode = BiosData.FanMode.Default,
                        GpuPower = BiosData.GpuPowerLevel.Med,
                        Levels = new byte[] { 40, 40 } };

            }
        }

        // The preset to show for the BIOS mode currently reported. The last preset
        // applied wins if its mode matches (Eco and Quiet share mode 3); otherwise the
        // preset named after the mode, or null if no preset uses it.
        public static PerformancePreset? ForMode(BiosData.FanMode mode, PerformancePreset? lastApplied) {
            if(lastApplied.HasValue && Resolve(lastApplied.Value).FanMode == mode)
                return lastApplied;
            switch(mode) {
                case BiosData.FanMode.LegacyQuiet:
                    return PerformancePreset.Quiet;
                case BiosData.FanMode.Default:
                    return PerformancePreset.Default;
                case BiosData.FanMode.Performance:
                    return PerformancePreset.Performance;
                default:
                    return null;
            }
        }

    }

}
