  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using OmenMon.Core;
using OmenMon.Hardware.Bios;
using OmenMon.Library;

namespace OmenMon.Hardware.Platform {

    // The fan-control sequences the GUI runs (main form, tray menu, monitor thread), moved
    // out of the forms so that the tests run exactly what the GUI sends. The GUI keeps the
    // dialogs and the hardware lock; callers hold GuiOp.HardwareLock.
    public static class FanActions {

        // What the main form's "Set" button asks for
        public enum Request {
            Program,
            Off,
            Max,
            Constant,
            Auto
        }

        // The main form's "Set": runs the selected mode. isFanMax / isFanOff are the
        // states read under the lock just before. For Constant, level0/level1 are the
        // trackbar levels (0 where the trackbar is at its minimum, as the form passes
        // them) and bothMin / bothMax whether both trackbars are at the end. For Auto,
        // applyPreset applies the chosen preset (GuiOp.ApplyPreset).
        public static void Apply(
            Request request,
            IFanArray fans,
            FanProgram program,
            bool isFanMax,
            bool isFanOff,
            string programName = null,
            byte level0 = 0,
            byte level1 = 0,
            bool bothMin = false,
            bool bothMax = false,
            Action applyPreset = null) {

            switch(request) {

                // Enable fan program
                case Request.Program:

                    if(isFanOff) // Re-enable fan if off first
                        fans.SetOff(false);

                    if(isFanMax) // Disable maximum speed first
                        fans.SetMax(false);

                    // Start the selected program
                    program.Run(programName);
                    break;

                // Switch off the fan
                case Request.Off:

                    // Terminate any running fan program
                    program.Terminate();

                    if(!isFanOff) { // Skip if already off

                        if(isFanMax) // Disable maximum speed first
                            fans.SetMax(false);

                        // Switch off the fan
                        fans.SetOff(true);

                    }
                    break;

                // Set fan to maximum speed
                case Request.Max:

                    // Terminate any running fan program
                    program.Terminate();

                    if(!isFanMax) { // Skip if already maximum speed

                        if(isFanOff) // Re-enable fan if off first
                            fans.SetOff(false);

                        // Set the fan to maximum speed
                        fans.SetMax(true);

                    }
                    break;

                // Enable fan constant speed
                case Request.Constant:

                    // Terminate any running fan program
                    program.Terminate();

                    // The BIOS mandates that at least one fan
                    // be left running at any given time, so if the user
                    // wants both of them off, we use another way to do so
                    if(bothMin) {

                        // Switch the fans off
                        if(!isFanOff) // If not already off
                            fans.SetOff(true);

                    // Conversely, if the user wants maximum speed setting
                    // for both fans, we just set it explicitly instead
                    } else if(bothMax) {

                        if(!isFanMax) // If not already at maximum speed
                            fans.SetMax(true);

                    // Otherwise, we just set the speed levels normally
                    } else {

                        if(isFanMax) // Disable maximum speed first
                            fans.SetMax(false);

                        if(isFanOff) // Re-enable fan if off first
                            fans.SetOff(false);

                        // Set each fan to the user-selected level
                        // or to zero, if the minimum value is selected
                        // (the fan-safety guard floors a zero while warm)
                        fans.SetLevels(new byte[] { level0, level1 });

                        // Re-send the fan mode so that the settings are applied (EC
                        // boards). Only the mode OmenMon itself last sent: re-sending the
                        // mode read back from the EC sent whatever byte the mode register
                        // held (8C77: 0x44, from the battery serial) to BIOS 0x1A
                        ReapplyMode(fans);

                    }
                    break;

                // Enable automatic fan mode (default)
                case Request.Auto:

                    // Terminate any running fan program
                    program.Terminate();

                    if(isFanOff) // Re-enable fan if off first
                        fans.SetOff(false);

                    if(isFanMax) // Disable maximum speed first
                        fans.SetMax(false);

                    // Hand back any OmenMon-held levels, then apply the preset
                    applyPreset?.Invoke();
                    break;

            }

        }

        // Re-sends the last mode OmenMon sent, on an EC model; nothing on a BIOS-only one
        // (its levels need no mode write) or if OmenMon has sent none
        public static void ReapplyMode(IFanArray fans) {
            if(fans.Control != FanControlKind.Ec)
                return;
            BiosData.FanMode? mode = fans.LastModeSent;
            if(mode.HasValue)
                fans.SetMode(mode.Value);
        }

        // The constant-speed countdown maintenance the monitor runs on every pass while
        // constant speed is active. EC model: extend the EC countdown when it runs low (and
        // re-send the last mode OmenMon sent, see ReapplyMode). BIOS-only: nothing; there
        // is no countdown register, and the 0x10 heartbeat keeps the level alive.
        public static void MaintainConstantSpeed(IFanArray fans) {
            if(fans.Control != FanControlKind.Ec)
                return;
            int countdown = fans.GetCountdown();
            if(countdown < Config.UpdateMonitorInterval + Config.FanCountdownExtendThreshold) {
                ReapplyMode(fans);
                fans.SetCountdown(Config.FanCountdownExtendInterval);
            }
        }

        // The tray menu's "Max Fan" toggle
        public static void ToggleMax(IFanArray fans, bool enable) {

            // Clear fan-off latch before enabling max so the BIOS will honour SetMax
            if(enable && fans.GetOff())
                fans.SetOff(false);

            // Toggle the maximum fan speed
            fans.SetMax(enable);

        }

        // The tray menu's "Fan Off" toggle
        public static void ToggleOff(IFanArray fans, bool off) {
            fans.SetOff(off);
        }

        // The fan part of a performance preset (GuiOp.ApplyPreset): hands back any
        // OmenMon-held levels, sets the preset's BIOS thermal policy and GPU power, runs
        // between (the refresh rate, which is not hardware), then its fixed levels through
        // the fan-safety guard
        public static void ApplyPreset(IFanArray fans, ISettings system, PresetSettings settings, Action between = null) {
            fans.Release();
            fans.SetMode(settings.FanMode);
            try {
                if(settings.GpuPower.HasValue)
                    system.SetGpuPower(new BiosData.GpuPowerData(settings.GpuPower.Value));
                between?.Invoke();
                if(settings.Levels != null)
                    fans.SetLevels(settings.Levels);
            } catch { }
        }

    }

}
