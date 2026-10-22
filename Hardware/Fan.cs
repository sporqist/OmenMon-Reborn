  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using OmenMon.Core;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Library;

namespace OmenMon.Hardware.Platform {

#region Interface
    // Defines an interface for interacting with a fan
    public interface IFan {

        // Retrieves the fan type
        public BiosData.FanType GetFanType();

        public int GetLevel();  // Retrieves the fan level [krpm]
        public int GetRate();   // Retrieves the fan rate [%]
        public int GetSpeed();  // Retrieves the fan speed [rpm]

        // Retrieves the byte the rate-write register holds now (what SetRate would
        // restore), -1 if it cannot be read
        public int GetRateWrite();

        public void SetLevel(int level);  // Sets the fan level [krpm]
        public void SetRate(int rate);    // Sets the fan rate [%]

    }
#endregion

    // Implements a mechanism for interacting with a fan
    public class Fan : IFan {

#region Implementation
        // Stores the fan type
        protected BiosData.FanType FanType;

        // Stores the level data component
        protected IPlatformReadWriteComponent Level;

        // Stores the rate data components (separate read and write)
        protected IPlatformReadComponent RateRead;
        protected IPlatformWriteComponent RateWrite;

        // Stores the speed data component
        protected IPlatformReadComponent Speed;

        // BIOS used for level queries (the shared Hw.Bios unless a test substitutes one)
        private readonly IBiosCtl bios;
        protected IBiosCtl Bios { get { return this.bios ?? Hw.Bios; } }

        // BIOS-only model: the speed is what BIOS 0x2D reports, in 100 rpm units (a
        // measured speed: on 8C77 it ramps through intermediate values and equals EC 0x11
        // FRPM, the fan-1 speed), whatever an AutoCal sidecar or the EC says (a
        // calibration on such a board pinned temperature bytes as RPM)
        private readonly bool speedFromBios;

        // BIOS-only model: this fan's maximum in the BIOS fan table (0x2F; 0 while it
        // cannot be read), against which its rate is shown (BiosFanControl.RatePercent)
        private readonly Func<int> rateMaximum;

        // Constructs a fan instance
        public Fan(
            BiosData.FanType type,
            IPlatformReadWriteComponent level,
            IPlatformReadComponent rateRead,
            IPlatformWriteComponent rateWrite,
            IPlatformReadComponent speed,
            IBiosCtl bios = null,
            bool speedFromBios = false,
            Func<int> rateMaximum = null) {

            this.bios = bios;
            this.speedFromBios = speedFromBios;
            this.rateMaximum = rateMaximum;
            this.FanType = type;
            this.Level = level;
            this.RateRead = rateRead;
            this.RateRead.SetConstraint(Config.MaxBelievablePercent);
            this.RateWrite = rateWrite;
            this.Speed = speed;
            this.Speed.SetConstraint(Config.FanLevelMax * (100 + Config.MaxBelievableFanSpeedPercentOverMax));

        }

        // Retrieves the fan type
        public virtual BiosData.FanType GetFanType() {
            return this.FanType;
        }

        // Retrieves the fan level [krpm]
        public virtual int GetLevel() {
            var levels = Hw.BiosGet(this.Bios.GetFanLevel);
            if(levels == null || levels.Length < (int) this.FanType) return 0;
            return levels[(int) this.FanType - 1];
        }

        // Retrieves the fan rate [%]. BIOS-only: the BIOS level against the fan table's
        // top row (-1 while that cannot be read), never an EC register
        public virtual int GetRate() {
            if(this.speedFromBios)
                return BiosFanControl.RatePercent(GetLevel(), this.rateMaximum != null ? this.rateMaximum() : 0);
            this.RateRead.Update();
            return this.RateRead.GetValue();
        }

        // Retrieves the rate-write register's byte, -1 if the register cannot be read
        // (write-only, absent on a BIOS-only model, or the read failed)
        public virtual int GetRateWrite() {
            if(!(this.RateWrite is IPlatformReadComponent read)
                || !read.GetAccessType().HasFlag(PlatformData.AccessType.Read)
                || read is AbsentComponent)
                return -1;
            return read.Update() ? read.GetValue() : -1;
        }

        // Retrieves the fan speed [rpm]
        //
        // If the Auto-Calibration Wizard has discovered a register/mode override for
        // this fan, the override wins over the PlatformPreset configured at construction.
        // We deliberately do not fall back to the preset on a transient zero — if the
        // override is wrong the user will see it immediately and can recalibrate, which
        // is far less confusing than a silent flip-flop between the two readings.
        public virtual int GetSpeed() {
            if(this.speedFromBios)
                return BiosFanControl.Rpm(GetLevel());

            // Pull (offset, mode, multiplier) as a single atomic snapshot — see AutoCal for why.
            byte reg = 0;
            EcDiffScanner.Mode mode = default(EcDiffScanner.Mode);
            int multiplier = 0;
            bool have =
                this.FanType == BiosData.FanType.Cpu ? AutoCal.TryGetCpu(out reg, out mode, out multiplier) :
                this.FanType == BiosData.FanType.Gpu ? AutoCal.TryGetGpu(out reg, out mode, out multiplier) :
                false;

            if(have) {
                // Boards with no real EC tachometer (e.g. 8C9C) advertise their RPM via
                // the BIOS-reported fan level × multiplier. AutoCal cannot service this
                // mode without a fan-type context, so we resolve it here using the same
                // GetLevel() call used by the slider — keeping a single source of truth.
                if(mode == EcDiffScanner.Mode.BiosLevelMirror)
                    return GetLevel() * (multiplier > 0 ? multiplier : 100);

                int rpm = AutoCal.ReadRpm(reg, mode, multiplier);
                if(rpm >= 0) return rpm;
            }

            this.Speed.Update();
            return this.Speed.GetValue();
        }

        // Sets the fan level [krpm]
        public virtual void SetLevel(int level) {
            this.Level.SetValue(level);
        }

        // Sets the fan rate [%]
        public virtual void SetRate(int rate) {
            this.RateWrite.SetValue(rate);
        }
#endregion

    }

}
