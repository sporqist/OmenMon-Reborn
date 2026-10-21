  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Library;

namespace OmenMon.Hardware.Platform {

#region Interface
    // Defines an interface for interacting with the fan system
    public interface IFanArray {

        public IFan[] Fan { get; }

        // Retrieves or sets the countdown value
        // until automatic settings are restored [s]
        public int GetCountdown();
        public void SetCountdown(int countdown);

        // Retrieves or sets the levels
        // of all fans at the same time
        // (writes go through the fan-safety guard, see FanSafetyGuard)
        public byte[] GetLevels();
        public void SetLevels(byte[] levels);
        public void SetLevels(byte[] levels, FanLevelSource source);

        // Fan-safety guard every level write goes through (null: none, legacy)
        public FanSafetyGuard Guard { get; set; }

        // Hands fan control back to the BIOS: a safe last level if OmenMon holds one,
        // then silence (without a guard, legacy: 255/255). ReleaseIfHeld() does nothing
        // if OmenMon holds no level.
        public void Release();
        public void ReleaseIfHeld();

        // Writes whatever the guard says is needed now (raise a level that is no
        // longer allowed, refresh the panic level); true if something was written
        public bool Supervise();

        // Enters or leaves level-based thermal panic (fan-table maximum)
        public void SetPanic(bool flag);

        // The thermal-panic level (fan-table maximum, or a safe fallback while the
        // table cannot be read); calling it early reads the table before it is needed
        public byte[] GetPanicLevel();

        // Whether the 0x10 heartbeat may run (false shortly after a hand-back)
        public bool HeartbeatAllowed { get; }

        // Whether Supervise() needs to run on every pass
        public bool NeedsSupervision { get; }

        // Retrieves or sets maximum fan speed
        public bool GetMax();  
        public void SetMax(bool flag);

        // Retrieves or sets manual fan control state
        public bool GetManual();
        public void SetManual(bool flag);

        // Retrieves or sets the current fan mode
        public BiosData.FanMode GetMode();
        public void SetMode(BiosData.FanMode mode);

        // Retrieves the fan off switch status
        // or switches the fan off
        public bool GetOff();
        public void SetOff(bool flag);


    }
#endregion

#region Implementation
    // Implements a mechanism for interacting with the fan system
    public class FanArray : IFanArray {

        // Fan array
        public IFan[] Fan { get; private set; }

        // Stores the countdown platform component
        protected IPlatformReadWriteComponent Countdown;

        // Stores the manual toggle component
        protected IPlatformReadWriteComponent Manual;

        // Per-model manual-mode trigger values (defaults match legacy FanManual.On/.Off).
        // Overridden via PlatformPreset for boards that gate manual fan control on a
        // non-standard register/value pair (e.g. 8BBE — Victus 16 R0053NT — writes 0x08
        // to EC[0x06] to engage, 0x48 to release). Set by the ctor — no inline default
        // here so the ctor's assignment is the single source of truth.
        private byte ManualValueOn;
        private byte ManualValueOff;

        // Stores the fan mode component
        protected IPlatformReadWriteComponent Mode;

        // Stores the fan on and off switch component
        protected IPlatformReadWriteComponent Switch;

        // BIOS used for fan calls (the shared Hw.Bios unless a test substitutes one)
        private readonly IBiosCtl bios;
        protected IBiosCtl Bios { get { return this.bios ?? Hw.Bios; } }

        // Fan-safety guard (set by Platform)
        public FanSafetyGuard Guard { get; set; }

        // Panic level cache (fan-table maximum, kept once read successfully)
        private byte[] panicLevel;

        // When the 0x2E fan-level call was last armed with a 0x10 query by this class
        private DateTime lastArmAt;

        // Constructs a fan array instance
        public FanArray(
            IFan[] fan,
            IPlatformReadWriteComponent fanCountdown,
            IPlatformReadWriteComponent fanManual,
            IPlatformReadWriteComponent fanMode,
            IPlatformReadWriteComponent fanSwitch,
            byte manualValueOn  = (byte) PlatformData.FanManual.On,
            byte manualValueOff = (byte) PlatformData.FanManual.Off,
            IBiosCtl bios = null) {

            this.bios = bios;
            this.ManualValueOn  = manualValueOn;
            this.ManualValueOff = manualValueOff;

            // Initialize the fan array
            this.Fan = new IFan[PlatformData.FanCount];

            // Define the CPU fan
            this.Fan[0] = fan[0];

            // Define the GPU fan
            this.Fan[1] = fan[1];

            // Define the countdown component
            this.Countdown = fanCountdown;

            // Define the mode component
            this.Manual = fanManual;

            // Define the mode component
            this.Mode = fanMode;

            // Define the switch component
            this.Switch = fanSwitch;

        }

        // Retrieves the countdown value [s]
        // until automatic settings are restored
        public int GetCountdown() {
            this.Countdown.Update();
            return this.Countdown.GetValue();
        }

        // Sets the countdown value [s]
        public void SetCountdown(int countdown) {
            this.Countdown.SetValue(countdown);
        }

        // Retrieves the levels of all fans at the same time
        public byte[] GetLevels() {
            return Hw.BiosGet(this.Bios.GetFanLevel);
        }

        // Sets the levels of all fans at the same time (a manual request)
        public void SetLevels(byte[] levels) {
            SetLevels(levels, FanLevelSource.Manual);
        }

        // Sets the levels of all fans at the same time, subject to the fan-safety guard.
        // 255/255 is the legacy "release" request and is handled as a hand-back.
        public void SetLevels(byte[] levels, FanLevelSource source) {

            if(levels[0] == FanSafetyGuard.NoChange && levels[1] == FanSafetyGuard.NoChange) {
                Release();
                return;
            }

            byte[] actual = this.Guard != null ? this.Guard.Filter(levels, source) : levels;
            WriteLevels(actual);
            this.Guard?.RecordWrite(levels, actual, source);

        }

        // Hands fan control back to the BIOS (see FanSafetyGuard.HandBackLevel for why
        // a last level is written first)
        public void Release() {

            if(this.Guard == null) {
                WriteLevels(new byte[] { FanSafetyGuard.NoChange, FanSafetyGuard.NoChange });
                return;
            }

            if(this.Guard.HoldsLevel) {
                ArmFanLevel(true);
                WriteLevels(this.Guard.HandBackLevel());
            }
            this.Guard.RecordRelease();

        }

        // Hands fan control back only if OmenMon holds a level (exit, crash, suspend)
        public void ReleaseIfHeld() {
            if(this.Guard != null && this.Guard.HoldsLevel)
                Release();
        }

        // Writes what the guard requires now
        public bool Supervise() {

            if(this.Guard == null)
                return false;

            byte[] level = this.Guard.Supervise();
            if(level == null)
                return false;

            ArmFanLevel(false);
            if(Config.FanLevelUseEc && this.GetOff())
                this.Switch.SetValue((int) PlatformData.FanSwitch.On);
            WriteLevels(level);
            this.Guard.RecordSupervisedWrite(level);
            return true;

        }

        // Enters or leaves level-based thermal panic. Entering writes the panic level at
        // once; leaving re-applies the last requested level, or hands control back to the
        // BIOS if nothing was requested (panic started from automatic mode).
        public void SetPanic(bool flag) {

            if(this.Guard == null)
                return;

            if(flag) {
                if(!this.Guard.IsPanic) {
                    this.Guard.SetPanic(GetPanicLevel());
                    Supervise();
                }
                return;
            }

            if(!this.Guard.IsPanic)
                return;
            this.Guard.SetPanic(null);
            byte[] requested = this.Guard.Requested;
            if(requested != null)
                SetLevels(requested, this.Guard.RequestedSource);
            else
                Release();

        }

        public bool HeartbeatAllowed {
            get { return this.Guard == null || this.Guard.HeartbeatAllowed; }
        }

        public bool NeedsSupervision {
            get { return this.Guard != null && this.Guard.NeedsSupervision; }
        }

        // The thermal-panic level: the highest level per fan in the BIOS fan table (0x2F),
        // which is what the firmware itself runs at the top of its curve (8BAC: 60/63).
        // Cached only once the table has been read and has a sane row (1-100) for each
        // fan. Otherwise, for a fan without one, the fallback: Config.FanLevelMax, but at
        // least the warm safety curve's top step (60/63 by default), the level the guard
        // gives an unknown temperature. Not cached, so the next panic reads the table
        // again: a single failed read must not hold the panic level down for the life of
        // the process.
        public byte[] GetPanicLevel() {

            if(this.panicLevel != null)
                return new byte[] { this.panicLevel[0], this.panicLevel[1] };

            byte[] table = new byte[2];
            try {
                BiosData.FanTable data = this.Bios.GetFanTable();
                if(data.Level != null)
                    foreach(BiosData.FanLevel row in data.Level) {
                        if(row.Fan1Level <= 100 && row.Fan1Level > table[0]) table[0] = row.Fan1Level;
                        if(row.Fan2Level <= 100 && row.Fan2Level > table[1]) table[1] = row.Fan2Level;
                    }
            } catch { }

            if(table[0] > 0 && table[1] > 0) {
                this.panicLevel = table;
                return new byte[] { table[0], table[1] };
            }

            FanSafetyPolicy policy = this.Guard != null ? this.Guard.Policy : new FanSafetyPolicy();
            byte max = (byte) Math.Max(1, Math.Min(100, Config.FanLevelMax));
            byte[] level = new byte[2];
            for(int i = 0; i < 2; i++)
                level[i] = table[i] > 0 ? table[i] : Math.Max(max, policy.ManualMinimum(i, null));
            return level;

        }

        // On boards like 8BAC a 0x2E fan-level call only takes effect within ~30 s after
        // a 0x10 fan-count query (normally the BIOS heartbeat). Safety-relevant writes
        // (hand-back, panic, raising a level the guard no longer allows) issue that query
        // themselves, at most every 20 s unless forced, so they work even while the
        // heartbeat is paused (battery) or quiet (after a hand-back).
        private void ArmFanLevel(bool force) {
            if(Config.FanLevelUseEc)
                return;
            DateTime now = this.Guard != null ? this.Guard.Now : DateTime.UtcNow;
            if(!force && now - this.lastArmAt < TimeSpan.FromSeconds(20))
                return;
            try {
                this.Bios.GetFanCount();
                this.lastArmAt = now;
            } catch { }
        }

        // Writes fan levels without any checks
        private void WriteLevels(byte[] levels) {

            // Set manual fan mode, if needed
            if(Config.FanLevelNeedManual)
                this.SetManual(true);

            // Depending on the configuration setting,
            // use either the BIOS or the EC to set levels
            if(Config.FanLevelUseEc) {

                // Try to set the speed for each fan individually
                for(int i = 0; i < levels.Length; i++)
                    this.Fan[i].SetLevel(levels[i]);

            } else {
                try {

                    // Make a WMI BIOS call to set the level of both fans
                    Hw.BiosSet(this.Bios.SetFanLevel, levels);

                } catch {

                    // It has been reported on some models the settings
                    // take effect anyway, despite a BIOS error returned

                    // Thus, silently ignore if the call failed

                    // Regardless of the Config.BiosErrorReporting value,
                    // status is always checked, and reported in CLI mode

                }
            }
        }

        // Retrieves the manual fan speed toggle status
        public bool GetManual() {
            return this.Manual.GetValue() == this.ManualValueOn;
        }

        // Sets the manual fan speed toggle status
        public void SetManual(bool flag) {
            this.Manual.SetValue(flag ? this.ManualValueOn : this.ManualValueOff);
        }

        // Retrieves the maximum fan speed status
        public bool GetMax() {
            return Hw.BiosGet<bool>(this.Bios.GetMaxFan);
        }

        // Sets the maximum fan speed status
        public void SetMax(bool flag) {
            Hw.BiosSet(this.Bios.SetMaxFan, flag);
        }

        // Retrieves the current fan mode
        public BiosData.FanMode GetMode() {
            this.Mode.Update();
            return (BiosData.FanMode) this.Mode.GetValue();
        }

        // Sets the current fan mode
        public void SetMode(BiosData.FanMode mode) {
            Hw.BiosSet<BiosData.FanMode>(this.Bios.SetFanMode, mode);
            // Note: WMI BIOS call preferred over this.Mode.SetValue((byte) mode);
        }

        // Retrieves the fan off switch status
        public bool GetOff() {
            this.Switch.Update();
            return ((PlatformData.FanSwitch) this.Switch.GetValue()) == PlatformData.FanSwitch.Off;
        }

        // Switches the fan off or back on. Switching off is a request for 0/0 and goes
        // through the fan-safety guard: while the system is not known to be cool (or once
        // the Fan Off time limit is up) the fans get the floor level instead.
        public void SetOff(bool flag) {
            if(flag && this.Guard != null) {
                byte[] off = new byte[] { 0, 0 };
                byte[] actual = this.Guard.Filter(off);
                if(actual[0] != 0 || actual[1] != 0) {
                    if(Config.FanLevelUseEc && this.GetOff())
                        this.Switch.SetValue((int) PlatformData.FanSwitch.On);
                    WriteLevels(actual);
                    this.Guard.RecordWrite(off, actual);
                    return;
                }
                this.Guard.RecordWrite(off, actual);
            }
            if(flag && !Config.FanLevelUseEc) {
                try {

                    // Use a BIOS call consistent with SetLevels when in BIOS fan level mode
                    Hw.BiosSet(this.Bios.SetFanLevel, new byte[] { 0, 0 });

                } catch {

                    // Settings may take effect anyway despite a BIOS error (see SetLevels)

                }
            } else {
                this.Switch.SetValue(flag ?
                    (int) PlatformData.FanSwitch.Off : (int) PlatformData.FanSwitch.On);
            }
        }

        // Checks if the current model has a known firmware issue with 100% fan speed.
        // Returns true for models where SetMax(true), the AutoCal wizard's 100% step,
        // or equivalent commands have been observed driving the EC past a BIOS-internal
        // rate-limiter, locking the fan controller until reboot.
        //
        // The common factor on every reported board has been a physical fan ceiling of
        // ~3600-3800 RPM, well below the headroom the calibration code path assumes.
        //
        //   8C30 — HP Victus 15-fb1000 (2023, AMD)        — issue #32 (NotDarkn)
        //   8D07 — HP Victus 15 sibling of 8C30 layout    — issue #56 (ghend-oss)
        //   8BAD — HP Victus 16 (2024, AMD)               — issue #58 (MartinSalg818)
        //   8E35 — HP Victus 15 (2026 BIOS)               — issue #57 (ClockworkNirvana,
        //          confirmed via raw EC-dump analysis: 70 % → 100 % shows 0 RPM gain on
        //          GPU and a 16 RPM regression on CPU — the same firmware signature.)
        //   8C77 — HP Omen 16-wf1012nl (single-fan SKU)   — issue #50 (stf1o, defensive add.
        //          PeriodEncoded8 reading at EC[0xD2] goes 0xB2 → 0xD0 → 0x99 → 0xEC across
        //          0/30/70/100 % — the 100 % period is slower than idle, same rate-limiter
        //          footprint as the boards above. RPM stays sidecar-supported only because
        //          the <Models> schema is LE16-only and cannot decode PeriodEncoded8.)
        //   88F4 — HP Omen (BIOS rate-limiter freeze)     — issue #67
        //   8748 — HP Omen 17-cb1004ur (2020)             — issue #111 (swo1och, AutoCal
        //          plateau at 70 %: CPU RPM flat at 2929 across 30 -> 70 %, the fan-ceiling
        //          signature; the period-encoded CPU tach at 0xDB is stuck at 0x50 from idle
        //          to max — the controller is already saturated.)
        //   8A50 — HP Victus 15-fa0031dx (2023, single-fan) — issue #115 (nean24, AutoCal
        //          plateau at 30 % and "max fan does nothing"; one ~1.6 kRPM fan at 0xB0,
        //          far below the headroom the 100 % path assumes.)
        //   8A4F — HP Victus 15 (2023)                    — issue #107 (richardcofie). AutoCal
        //          plateau-detector tripped at 30 %: live RPM collapsed to idle (CPU 5109→1585,
        //          GPU 4418→1597) instead of climbing, the physical-ceiling signature. Fan regs
        //          are PeriodEncoded8 (CPU 0x2E, GPU 0x2F), so — like 8C77 — RPM stays
        //          sidecar-only and this list is the protection that applies.
        //
        // Call-sites:
        //   App/Cli/CliOpCalibration.cs — AutoCal wizard filters 100% out of its profile.
        //   App/Gui/GuiOp.cs            — GUI gates manual SetMax(true) behind a confirm.
        // Adding a ProductId here propagates to every call-site automatically.
        //
        // The wizard's plateau detector (CliOpCalibration.AutoCalibrate) is the boardless
        // root-cause fix and stops the freeze for unknown boards too; this list stays as
        // defence-in-depth for boards we've already confirmed.
        public static bool HasMaxFanFreeze(string productId) {
            switch(productId) {
                case "8C30":
                case "8D07":
                case "8BAD":
                case "8E35":
                case "8C77":
                case "88F4":
                case "8748":
                case "8A50":
                case "8A4F":
                case "8C9C":
                case "8BAB":
                case "8BD4":
                case "8D3F":
                case "8600":
                case "8BC2":
                case "8E41":
                case "8D24":
                case "8902":
                case "88FD":
                    return true;
                default:
                    return false;
            }
        }
#endregion

    }

}
