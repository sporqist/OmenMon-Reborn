  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;

namespace OmenMon.Core {

    // What one fan-level value does to its fan on a board
    public enum FanLevelEffect {
        Set,   // the fan runs at this level [rpm / 100]
        Off,   // the fan stops: 0 on a board where 0 is off, not automatic
        Keep   // the fan stays where it is (255)
    }

    // How one board's firmware handles fan control, as measured on that board: which BIOS
    // calls may be written, how long a fan level holds, and what the special values mean.
    // Data and pure functions only; nothing here sends anything, and nothing enforces it
    // yet (the service's BIOS adapter is to refuse every type not in WriteTypes). Valid
    // for the BIOS version it names: after a BIOS update the hardware gate runs again.
    public sealed class BoardProfile {

        // Board (WMI baseboard product) and the BIOS version the profile was measured on
        public string ProductId { get; }
        public string ValidatedBios { get; }

        // The BIOS WMI command for fan calls (hpqBIntM, signature "SECU") and its types
        public uint Command { get; }
        public uint FanCountType { get; }  // arms a following fan-level write, keeps a level alive
        public uint FanLevelType { get; }  // sets the fan levels [cpu, gpu], rpm / 100

        // A fan-level write only takes effect this long after a fan-count query
        public TimeSpan ArmWindow { get; }

        // A fan level holds this long after the last fan-level write or fan-count query,
        // then the BIOS curve resumes. There is no release command: releasing is staying
        // silent for longer than this.
        public TimeSpan LevelExpiry { get; }

        // Whether 0 stops a fan (true on 8BAC: 0/0 is both fans off, not automatic)
        public bool ZeroIsOff { get; }

        // The value that leaves a fan where it is (255/255: no change at all)
        public byte KeepLevel { get; }

        private readonly uint[] writeTypes;

        public BoardProfile(
            string productId,
            string validatedBios,
            uint command,
            uint fanCountType,
            uint fanLevelType,
            uint[] writeTypes,
            TimeSpan armWindow,
            TimeSpan levelExpiry,
            bool zeroIsOff,
            byte keepLevel) {

            this.ProductId = productId ?? throw new ArgumentNullException(nameof(productId));
            this.ValidatedBios = validatedBios;
            this.Command = command;
            this.FanCountType = fanCountType;
            this.FanLevelType = fanLevelType;
            this.writeTypes = (uint[]) (writeTypes ?? new uint[0]).Clone();
            this.ArmWindow = armWindow;
            this.LevelExpiry = levelExpiry;
            this.ZeroIsOff = zeroIsOff;
            this.KeepLevel = keepLevel;

        }

        // The command types that may be written (a copy)
        public uint[] WriteTypes {
            get { return (uint[]) this.writeTypes.Clone(); }
        }

        // Whether a command type may be written. Anything else is refused: the firmware
        // checks no bounds, and many types go straight to SMI (0x52 MUX, 0x53 NVRAM,
        // 0x29 power limits).
        public bool MayWrite(uint type) {
            return Array.IndexOf(this.writeTypes, type) >= 0;
        }

        // What one fan's level value does
        public FanLevelEffect Effect(byte level) {
            if(level == this.KeepLevel)
                return FanLevelEffect.Keep;
            if(level == 0 && this.ZeroIsOff)
                return FanLevelEffect.Off;
            return FanLevelEffect.Set;
        }

        // Whether a fan-level write changes nothing (both fans keep where they are)
        public bool IsNoChange(byte cpu, byte gpu) {
            return Effect(cpu) == FanLevelEffect.Keep && Effect(gpu) == FanLevelEffect.Keep;
        }

        // Whether a fan-level write stops both fans
        public bool IsOff(byte cpu, byte gpu) {
            return Effect(cpu) == FanLevelEffect.Off && Effect(gpu) == FanLevelEffect.Off;
        }

        // Whether a fan-level write at the given time takes effect after a fan-count
        // query at lastFanCount (null: none)
        public bool IsArmed(DateTime? lastFanCount, DateTime now) {
            return lastFanCount.HasValue && now - lastFanCount.Value <= this.ArmWindow;
        }

        // When a level last refreshed (fan-level write or fan-count query) at the given
        // time stops holding
        public DateTime LevelExpiresAt(DateTime lastRefresh) {
            return lastRefresh + this.LevelExpiry;
        }

        // HP OMEN 16-wf0xxx (8BAC), BIOS F.30, measured 2026-10-15/16 and 2026-10-21:
        //   * 0x10 arms a following 0x2E for at least 30 s, and keeps an active level alive;
        //   * a level expires 123-124 s after the last 0x2E or 0x10;
        //   * 0x2E [0, 0] switches both fans off;
        //   * 255 leaves that fan where it is, 255/255 changes nothing.
        // Writes: 0x2E and 0x10 only (restructuring plan, section 3a.4); others are added one
        // by one, each after its own hardware gate.
        public static readonly BoardProfile Omen8Bac = new BoardProfile(
            "8BAC", "F.30",
            0x20008, 0x10, 0x2E,
            new uint[] { 0x10, 0x2E },
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(124),
            true,
            0xFF);

        // The profile for a board, or null if there is none (the board is not measured)
        public static BoardProfile Find(string productId) {
            if(productId != null && productId.Trim().Equals(Omen8Bac.ProductId, StringComparison.OrdinalIgnoreCase))
                return Omen8Bac;
            return null;
        }

    }

}
