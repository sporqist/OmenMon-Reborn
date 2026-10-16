  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Hardware.Ec;

namespace OmenMon.Tests.Fakes {

    // A 256-byte Embedded Controller register file. On 8BAC, 0xB2 holds the CPU and
    // 0xB4 the GPU temperature (a parked GPU reads 0). LockFails simulates the EC
    // mutex timing out: nothing is read or written and the call reports failure.
    public sealed class FakeEcRegisters : IEcRegisters {

        public const byte CpuTempRegister = 0xB2;
        public const byte GpuTempRegister = 0xB4;

        public byte[] Data { get; } = new byte[256];

        public bool LockFails { get; set; }

        public List<(byte Register, byte Value)> Writes { get; } = new List<(byte, byte)>();

        public byte CpuTemperature {
            get { return this.Data[CpuTempRegister]; }
            set { this.Data[CpuTempRegister] = value; }
        }

        public byte GpuTemperature {
            get { return this.Data[GpuTempRegister]; }
            set { this.Data[GpuTempRegister] = value; }
        }

        public bool TryReadByte(byte register, out byte value) {
            if(this.LockFails) {
                value = 0;
                return false;
            }
            value = this.Data[register];
            return true;
        }

        public bool TryReadWord(byte register, out ushort value) {
            if(this.LockFails) {
                value = 0;
                return false;
            }
            value = (ushort) (this.Data[register] | (this.Data[(byte) (register + 1)] << 8));
            return true;
        }

        public bool TryWriteByte(byte register, byte value) {
            if(this.LockFails)
                return false;
            this.Data[register] = value;
            this.Writes.Add((register, value));
            return true;
        }

        public bool TryWriteWord(byte register, ushort value) {
            if(this.LockFails)
                return false;
            TryWriteByte(register, (byte) value);
            TryWriteByte((byte) (register + 1), (byte) (value >> 8));
            return true;
        }

    }

}
