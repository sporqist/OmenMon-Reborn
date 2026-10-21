  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;

namespace OmenMon.Core {

    // A fan level [rpm / 100] for the CPU and the GPU fan that has passed the fan-safety
    // rules. Only the core constructs one (FanSafetyGuard.Allow and friends), so code that
    // takes a SafeLevel can only be handed a level the guard decided on: the type is the
    // proof that the rules were applied (restructuring plan, rule 3).
    //
    //   * 0/0 (fans off) can be a SafeLevel: the guard allows it while the system is known
    //     to be cool (Fan Off, program rows of 0), and the rules decide that, not the type.
    //   * 255 (FanSafetyGuard.NoChange) for a fan means "leave this fan where it is".
    //   * default(SafeLevel) is not a level: IsValid is false and reading it throws, so a
    //     forgotten initialisation can never reach the fans as 0/0.
    public readonly struct SafeLevel : IEquatable<SafeLevel> {

        private readonly byte cpu;
        private readonly byte gpu;
        private readonly bool valid;

        internal SafeLevel(byte cpu, byte gpu) {
            this.cpu = cpu;
            this.gpu = gpu;
            this.valid = true;
        }

        internal SafeLevel(byte[] level) : this(level[0], level[1]) {
        }

        // False for default(SafeLevel), which no rule produced
        public bool IsValid { get { return this.valid; } }

        public byte Cpu { get { return Valid().cpu; } }
        public byte Gpu { get { return Valid().gpu; } }

        // The level as the two bytes the BIOS call takes ([CPU, GPU]), a new array
        public byte[] ToArray() {
            Valid();
            return new byte[] { this.cpu, this.gpu };
        }

        private SafeLevel Valid() {
            if(!this.valid)
                throw new InvalidOperationException("Not a level the fan-safety rules produced");
            return this;
        }

        public bool Equals(SafeLevel other) {
            return this.valid == other.valid && this.cpu == other.cpu && this.gpu == other.gpu;
        }

        public override bool Equals(object obj) {
            return obj is SafeLevel other && Equals(other);
        }

        public override int GetHashCode() {
            return this.valid ? (this.cpu << 8 | this.gpu) : -1;
        }

        public override string ToString() {
            return this.valid ? this.cpu + "/" + this.gpu : "none";
        }

    }

}
