  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Library;

namespace OmenMon.Hardware.Ec {

    // Register-level Embedded Controller access as used by the sensor and fan
    // components. This is a seam only: it sits above the EC transaction code
    // (EmbeddedController, Ring0/PawnIO, the Global\Access_EC mutex), which it
    // does not replace. Every call reports whether it actually ran — false means
    // the EC lock could not be obtained (or there is no EC), or for a byte read that
    // the EC handshake failed on every retry, so callers can tell a failed read from
    // a genuine zero.
    public interface IEcRegisters {

        public bool TryReadByte(byte register, out byte value);
        public bool TryReadWord(byte register, out ushort value);

        public bool TryWriteByte(byte register, byte value);
        public bool TryWriteWord(byte register, ushort value);

    }

    // Production implementation: forwards to the same Hw accessors the components
    // have always used, so the EC lock request, the register read/write and the lock
    // release happen exactly as before
    public sealed class HwEcRegisters : IEcRegisters {

        public static readonly HwEcRegisters Instance = new HwEcRegisters();

        private HwEcRegisters() { }

        public bool TryReadByte(byte register, out byte value) {
            return Hw.TryEcGetByte(register, out value);
        }

        public bool TryReadWord(byte register, out ushort value) {
            return Hw.TryEcGetWord(register, out value);
        }

        public bool TryWriteByte(byte register, byte value) {
            return Hw.TryEcSetByte(register, value);
        }

        public bool TryWriteWord(byte register, ushort value) {
            return Hw.TryEcSetWord(register, value);
        }

    }

}
