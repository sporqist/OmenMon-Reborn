  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using OmenMon.Library;

namespace OmenMon.Hardware.Platform {

    // Who asked for a fan level. Programs re-evaluate the temperature every tick, so
    // their low levels are not time-limited; everything else (constant speed, Fan Off,
    // presets, CLI, calibration) is.
    public enum FanLevelSource {
        Manual,
        Program
    }

    // The tunable part of the fan-safety rules
    public sealed class FanSafetyPolicy {

        // Minimum level per fan [rpm / 100] whenever the system is not known to be cool
        public byte FloorCpu = 20;
        public byte FloorGpu = 22;

        // Below this (fresh, valid CPU reading; GPU too if it reads) the floor is waived [°C]
        public int CoolTemperature = 50;

        // How long a manual level below the floor (Fan Off, constant minimum) may stay
        // in effect even while cool
        public TimeSpan BelowFloorMaxDuration = TimeSpan.FromMinutes(15);

        // After handing control back to the BIOS, how long the 0x10 heartbeat must stay
        // quiet for the last level to expire (8BAC: ~124 s after the last 0x2E/0x10)
        public TimeSpan HandBackQuietPeriod = TimeSpan.FromSeconds(130);

        public byte Floor(int fan) {
            return fan == 0 ? this.FloorCpu : this.FloorGpu;
        }

        public static FanSafetyPolicy FromConfig() {
            return new FanSafetyPolicy {
                FloorCpu = Config.FanSafetyFloorCpu,
                FloorGpu = Config.FanSafetyFloorGpu,
                CoolTemperature = Config.FanSafetyCoolTemperature,
                BelowFloorMaxDuration = TimeSpan.FromMinutes(Config.FanOffMaxMinutes) };
        }

    }

    // The single set of rules every fan-level write goes through (FanArray applies it
    // to programs, constant speed, Fan Off, presets, calibration, thermal panic; the CLI
    // FanLevel command applies it directly). It decides, it does not do I/O.
    //
    // Rules:
    //   * Floor: each fan gets at least its floor level unless the system is known to be
    //     cool — a fresh, valid CPU reading below CoolTemperature (and the GPU below it
    //     too, if the GPU reads). An unknown temperature is never cool. 0/0 (fans off) is
    //     therefore impossible while warm or while the sensors do not read.
    //   * Time limit: a manual level below the floor that is allowed because the system
    //     is cool is raised to the floor after BelowFloorMaxDuration.
    //   * Panic: while thermal panic is active every write is raised to the panic level
    //     (the fan-table maximum), and Supervise() re-sends it on every pass.
    //   * Hand-back: see HandBackLevel().
    //
    // 255 (0xFF) is the "no change" sentinel and is passed through untouched.
    public sealed class FanSafetyGuard {

        public const byte NoChange = 0xFF;

        public FanSafetyPolicy Policy { get; }

        private readonly Func<SensorReading> cpuReading;
        private readonly Func<SensorReading> gpuReading;
        private readonly Func<DateTime> clock;

        // Last level asked for (before the rules), null if none or handed back
        private byte[] requested;
        private FanLevelSource requestedSource;

        // Last level actually written (after the rules), null if none or handed back
        private byte[] written;

        // Since when a time-limited level below the floor has been in effect
        private DateTime belowFloorSince;

        // Thermal panic level, null if not in panic
        private byte[] panicLevel;

        // When control was last handed back to the BIOS
        private DateTime releasedAt;
        private bool released;

        public FanSafetyGuard(
            FanSafetyPolicy policy,
            Func<SensorReading> cpuReading,
            Func<SensorReading> gpuReading,
            Func<DateTime> clock) {

            this.Policy = policy ?? new FanSafetyPolicy();
            this.cpuReading = cpuReading ?? (() => SensorReading.None);
            this.gpuReading = gpuReading ?? (() => SensorReading.None);
            this.clock = clock ?? (() => DateTime.UtcNow);

        }

        public DateTime Now { get { return this.clock(); } }

        public byte[] Requested { get { return Copy(this.requested); } }
        public FanLevelSource RequestedSource { get { return this.requestedSource; } }
        public byte[] Written { get { return Copy(this.written); } }
        public bool IsPanic { get { return this.panicLevel != null; } }

        // Whether OmenMon currently holds the fans at a level it set
        public bool HoldsLevel { get { return this.written != null; } }

        // Whether periodic supervision is needed: panic is active, or a level below the
        // floor is in effect (it must be raised as soon as the system warms up or the
        // sensors stop reading, or when its time is up)
        public bool NeedsSupervision {
            get {
                return this.panicLevel != null
                    || (this.written != null && IsBelowFloor(this.written));
            }
        }

        // Whether the 0x10 keep-alive may run. False for HandBackQuietPeriod after a
        // hand-back, so the last level can expire and the BIOS curve takes over.
        public bool HeartbeatAllowed {
            get {
                return !(this.released && this.Now - this.releasedAt < this.Policy.HandBackQuietPeriod);
            }
        }

        // Whether the readings say the system is cool enough to go below the floor
        public bool IsCool() {
            DateTime now = this.Now;
            SensorReading cpu = this.cpuReading();
            if(!cpu.IsFresh(now, Platform.ReadingMaxAge) || cpu.Value >= this.Policy.CoolTemperature)
                return false;
            SensorReading gpu = this.gpuReading();
            if(gpu.IsFresh(now, Platform.ReadingMaxAge) && gpu.Value >= this.Policy.CoolTemperature)
                return false;
            return true;
        }

        // Applies the rules to a requested level and returns what may be written
        public byte[] Filter(byte[] level, FanLevelSource source = FanLevelSource.Manual) {

            if(level == null || level.Length < 2)
                throw new ArgumentException("Two fan levels expected", nameof(level));

            byte[] result = new byte[] { level[0], level[1] };
            bool cool = IsCool();
            bool timeUp = source == FanLevelSource.Manual
                && this.belowFloorSince != default(DateTime)
                && this.Now - this.belowFloorSince >= this.Policy.BelowFloorMaxDuration;

            for(int i = 0; i < 2; i++) {

                if(result[i] == NoChange)
                    continue;

                if(this.panicLevel != null && result[i] < this.panicLevel[i])
                    result[i] = this.panicLevel[i];

                byte floor = this.Policy.Floor(i);
                if(result[i] < floor && (!cool || timeUp))
                    result[i] = floor;

            }

            return result;

        }

        // Records a request and the level actually written for it
        public void RecordWrite(byte[] request, byte[] level, FanLevelSource source = FanLevelSource.Manual) {

            this.released = false;
            this.requested = Copy(request);
            this.requestedSource = source;

            // A "no change" write keeps the previous level in effect
            if(level[0] == NoChange && level[1] == NoChange)
                return;
            this.written = new byte[] {
                level[0] == NoChange && this.written != null ? this.written[0] : level[0],
                level[1] == NoChange && this.written != null ? this.written[1] : level[1] };

            if(source == FanLevelSource.Manual && IsBelowFloor(this.written)) {
                if(this.belowFloorSince == default(DateTime))
                    this.belowFloorSince = this.Now;
            } else {
                this.belowFloorSince = default(DateTime);
            }

        }

        // Records a level written by supervision (Supervise()); the request it serves
        // stays what it was, including none (panic entered from automatic mode)
        public void RecordSupervisedWrite(byte[] level) {
            byte[] request = this.requested;
            RecordWrite(request ?? level, level, this.requestedSource);
            this.requested = request;
        }

        // Returns the level to write now to keep the rules satisfied (raise a level
        // that is no longer allowed, refresh the panic level), or null if none
        public byte[] Supervise() {

            if(this.panicLevel != null)
                return Filter(this.requested ?? new byte[] { 0, 0 }, this.requestedSource);

            if(this.written == null)
                return null;

            byte[] allowed = Filter(this.written, this.requestedSource);
            return allowed[0] != this.written[0] || allowed[1] != this.written[1] ? allowed : null;

        }

        // Enters or leaves thermal panic. Leaving it, the caller re-applies Requested
        // (if any) or hands control back.
        public void SetPanic(byte[] level) {
            this.panicLevel = Copy(level);
        }

        // The last level to leave in effect when handing control back to the BIOS.
        //
        // Why a level and not "auto": on 8BAC there is no release command. 255/255 keeps
        // the current speed, and 0/0 is fans OFF. A level set with 0x2E stays in effect
        // until about 124 s after the last 0x2E or 0x10 call, then the BIOS curve
        // resumes. So handing back means: write a safe level, stop refreshing it (no
        // more 0x2E, and no 0x10 heartbeat for HandBackQuietPeriod), and let it expire.
        // The level is the last one written, but never below the floor: if OmenMon had
        // the fans off, the two minutes until the BIOS takes over run at the floor
        // instead of with the fans stopped. On boards where 255/255 does mean "auto",
        // the 255/255 written after this level hands control back immediately.
        public byte[] HandBackLevel() {
            byte[] level = new byte[2];
            for(int i = 0; i < 2; i++) {
                byte floor = this.Policy.Floor(i);
                byte last = this.written == null || this.written[i] == NoChange ? floor : this.written[i];
                level[i] = last > floor ? last : floor;
            }
            return level;
        }

        // Records that control was handed back to the BIOS
        public void RecordRelease() {
            this.released = true;
            this.releasedAt = this.Now;
            this.requested = null;
            this.written = null;
            this.belowFloorSince = default(DateTime);
        }

        private bool IsBelowFloor(byte[] level) {
            for(int i = 0; i < 2; i++)
                if(level[i] != NoChange && level[i] < this.Policy.Floor(i))
                    return true;
            return false;
        }

        private static byte[] Copy(byte[] level) {
            return level == null ? null : new byte[] { level[0], level[1] };
        }

    }

}
