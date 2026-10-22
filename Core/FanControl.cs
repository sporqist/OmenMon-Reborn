  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;

namespace OmenMon.Core {

    // How a board's fans are controlled
    public enum FanControlKind {

        // A model whose EC fan registers are used as configured (the legacy path): rate,
        // countdown, manual, mode and switch registers are read and, depending on the
        // settings, written
        Ec,

        // BIOS only: levels through 0x10/0x2E (via the fan-safety guard), the fan speed is
        // the 0x2D speed x 100, temperatures come from BIOS 0x23 and the EC registers the
        // model names. No EC fan register is read or written. For every board without a
        // verified EC map: on 8C77 (measured) the default fan registers point into ID,
        // serial and SMBus bytes.
        Bios

    }

    // The decisions of BIOS-only fan control, without I/O
    public static class BiosFanControl {

        // How long a fan level holds after the last 0x10/0x2E (8BAC and 8C77: ~124 s,
        // see BoardProfile.LevelExpiry). Used for the countdown a BIOS-only board shows.
        public static readonly TimeSpan DefaultLevelHold = TimeSpan.FromSeconds(124);

        // 0x2D reports each fan's speed in units of 100 rpm, measured: on 8BAC it ramps over
        // ~15 s after a set and jitters at steady state; on 8C77 it ramps through
        // intermediate values and its CPU byte equals EC 0x11 (FRPM, the fan-1 speed)
        public const int RpmPerLevel = 100;

        // The speed shown for a 0x2D level [rpm]
        public static int Rpm(int level) {
            return level <= 0 ? 0 : level * RpmPerLevel;
        }

        // The rate a BIOS-only board shows for a fan [%]: its 0x2D level against that fan's
        // maximum in the BIOS fan table (0x2F, the top row; 60/63 on 8BAC and 8C77), as a
        // whole percentage, at most 100. -1 (unknown) without a sane maximum (1-100) or a
        // level. Nothing from the EC: there is no rate register (and 8C77's EC 0x14 FWPM
        // keeps the last 0x2E level after it expired, so it is no rate either).
        public static int RatePercent(int level, int tableMaximum) {
            if(level < 0 || tableMaximum < 1 || tableMaximum > 100)
                return -1;
            int percent = (level * 100 + tableMaximum / 2) / tableMaximum;
            return percent > 100 ? 100 : percent;
        }

        // Seconds a held level has left: from the last 0x10/0x2E OmenMon sent; 0 if it
        // holds none (null) or the time is up
        public static int HoldSecondsLeft(DateTime? lastRefresh, DateTime now, TimeSpan hold) {
            if(!lastRefresh.HasValue)
                return 0;
            double left = (lastRefresh.Value + hold - now).TotalSeconds;
            return left <= 0 ? 0 : (int) Math.Ceiling(left);
        }

        // Whether the calibration wizard may run. It writes EC registers (manual, switch,
        // countdown, rate) and searches the EC for a tachometer, so only on a model whose
        // EC map is verified: an entry of kind Ec (modelControl; null: no entry), with
        // the fans running on it. Never on a BIOS-only board (its speed is BIOS 0x2D,
        // nothing to calibrate) nor on a board without an entry.
        public static bool CalibrationAllowed(FanControlKind? modelControl, FanControlKind running) {
            return modelControl == FanControlKind.Ec && running == FanControlKind.Ec;
        }

        // Whether the level OmenMon holds has both fans off (0/0)
        public static bool IsOff(byte[] written) {
            return written != null && written.Length >= 2 && written[0] == 0 && written[1] == 0;
        }

        // The temperature sensors of a BIOS-only model: BIOS 0x23 (CPU), and the EC
        // registers the model names (TempCpuReg as "CPUT", TempGpuReg as "GPTM"; 0: none).
        // Nothing else: the global <Temperature> sensors (CPUT 0x57, GPTM 0xB7, RTMP 0x58,
        // TMP1 0x59, TNTx) are other data on these boards (on 8C77 0x57 is the keyboard
        // colour, 0x58 flag bits, 0x59 the thermal policy, which reads "80 °C" in Cool
        // mode), so they feed neither the display, the maximum, panic nor the programs.
        // Every sensor listed counts for the maximum temperature. gpuFromDriver: a board
        // without a GPU register (an unknown board) reads the GPU from the graphics
        // driver (NVML) while the dGPU is awake.
        public static TemperatureSensorPlan[] Sensors(byte tempCpuReg, byte tempGpuReg, bool gpuFromDriver = false) {
            var plan = new System.Collections.Generic.List<TemperatureSensorPlan>();
            if(tempCpuReg != 0)
                plan.Add(new TemperatureSensorPlan("CPUT", TemperatureSource.Ec, tempCpuReg));
            if(tempGpuReg != 0)
                plan.Add(new TemperatureSensorPlan("GPTM", TemperatureSource.Ec, tempGpuReg));
            else if(gpuFromDriver)
                plan.Add(new TemperatureSensorPlan("GPTM", TemperatureSource.GpuDriver, 0));
            plan.Add(new TemperatureSensorPlan("BIOS", TemperatureSource.Bios, 0));
            return plan.ToArray();
        }

    }

    // A model's extra temperatures, for display only: named EC registers (the ACPI field
    // names, e.g. CTMP, SKTB, EST1, EST2, CATT) shown next to the others. They feed
    // nothing: not the maximum that drives the fans, not thermal panic, not the fan
    // programs, not the fan-safety guard (CPU, GPU and BIOS 0x23 stay the safety
    // sources). A value outside 1-120 °C is shown as invalid, never as a temperature.
    public static class DisplayTemperature {

        public const int MinValid = 1;
        public const int MaxValid = 120;

        // What an invalid value (or a failed read) is shown as
        public const int Invalid = -1;

        public static bool IsValid(int celsius) {
            return celsius >= MinValid && celsius <= MaxValid;
        }

        // What is shown for a read: the temperature, or Invalid
        public static int Shown(bool readOk, int value) {
            return readOk && IsValid(value) ? value : Invalid;
        }

        // The names the fan logic looks sensors up by: a display sensor must never have
        // one of them (it would then feed the CPU, GPU or BIOS reading)
        private static readonly string[] Reserved = { "CPUT", "GPTM", "BIOS" };

        // A name is 1-8 letters or digits and not reserved
        public static bool IsValidName(string name) {
            if(string.IsNullOrEmpty(name) || name.Length > 8)
                return false;
            foreach(char c in name)
                if(!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9'))
                    return false;
            foreach(string reserved in Reserved)
                if(string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

    }

    // Where a temperature reading comes from
    public enum TemperatureSource {
        Bios,       // BIOS 0x23
        Ec,         // an EC register
        GpuDriver   // the graphics driver (NVML), only while the dGPU is awake
    }

    // One temperature sensor to set up: its name (what the fan logic looks it up by),
    // its source and, for the EC, the register
    public readonly struct TemperatureSensorPlan {

        public readonly string Name;
        public readonly TemperatureSource Source;
        public readonly byte Register;

        public TemperatureSensorPlan(string name, TemperatureSource source, byte register) {
            this.Name = name;
            this.Source = source;
            this.Register = register;
        }

        public override string ToString() {
            return this.Name + " (" + this.Source + (this.Source == TemperatureSource.Ec ? " 0x" + this.Register.ToString("X2") : "") + ")";
        }

    }

    // F3: a board without a model entry. If the BIOS fan interface answers (a fan count
    // of at least 1 from 0x10, the levels readable through 0x2D, thermal policy V1, as on
    // 8BAC and 8C77) it runs BIOS-only, with the CPU temperature from 0x23 and the GPU
    // from the graphics driver. Otherwise the legacy EC default layout, with a warning.
    public static class UnknownBoard {

        public static FanControlKind Decide(int? fanCount, byte[] levels, bool thermalPolicyV1) {
            return fanCount.HasValue && fanCount.Value >= 1
                && levels != null && levels.Length >= 2
                && thermalPolicyV1
                ? FanControlKind.Bios : FanControlKind.Ec;
        }

    }

    // Which fan mode may be sent again (a fan program restoring the mode it found, constant
    // speed re-sending the mode so the levels apply). A mode read from the EC is sent only
    // from a mode register verified to hold what BIOS 0x1A last sent: on 8BAC and 8C77 the
    // ACPI method behind 0x1A (GC1A) writes its data[1] verbatim into EC 0x59 (OGHM) and
    // nothing else writes it. Any other register is not a mode (8C77's default ModeReg
    // 0x95 is in the battery name and reads 0x44, and 0x1A with that changes the power
    // limits), so without a verified register only the mode OmenMon itself last sent is
    // re-sent, or nothing.
    public static class ModeReplay {

        // What a verified mode register holds while no 0x1A has run since boot (8C77 reads
        // 0x00 then): the BIOS default, not a mode. 0x00 is a defined mode number
        // (LegacyDefault), but sending it is not a replay: on AC 0x1A also sets the power
        // limits (bit 0 clear: PL1 55 W), which the boot default need not have
        public const int NoModeSinceBoot = 0x00;

        // The mode a verified register shows: its value, or null (unknown) if the read
        // failed or it holds NoModeSinceBoot
        public static int? RegisterMode(bool readOk, int value) {
            if(!readOk || value == NoModeSinceBoot || value < 0 || value > 0xFF)
                return null;
            return value;
        }

        // The mode to send again: from a verified register, its mode if it is a defined
        // one (an undefined byte: nothing); if the verified register shows none (failed
        // read, nothing since boot), or there is no verified register, the last mode
        // OmenMon sent (null: nothing)
        public static int? Choose(bool registerVerified, int? registerMode, int? lastSent, Func<int, bool> isDefined) {
            if(registerVerified && registerMode.HasValue)
                return isDefined != null && isDefined(registerMode.Value) ? registerMode : null;
            return lastSent;
        }

    }

    // Printable ASCII in an EC image: a register inside a run of printable characters is
    // most likely part of a string (an ID, a battery name, the EC's SMBus buffer), not a
    // fan or temperature register. Temperatures between 32 and 126 are printable too, so
    // this refuses some real sensors; refusing is the safe side.
    public static class PrintableRun {

        // The shortest run that counts as a string
        public const int MinRun = 4;

        public static bool IsPrintable(byte value) {
            return value >= 0x20 && value <= 0x7E;
        }

        // Whether any of the size bytes at index, or the byte on either side of them, is
        // part of a run of at least minRun printable bytes
        public static bool Touches(byte[] image, int index, int size = 1, int minRun = MinRun) {
            if(image == null)
                return false;
            for(int i = index - 1; i <= index + size; i++) {
                if(i < 0 || i >= image.Length || !IsPrintable(image[i]))
                    continue;
                int from = i, to = i;
                while(from > 0 && IsPrintable(image[from - 1])) from--;
                while(to < image.Length - 1 && IsPrintable(image[to + 1])) to++;
                if(to - from + 1 >= minRun)
                    return true;
            }
            return false;
        }

    }

}
