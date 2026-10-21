  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Hardware.Ec;

namespace OmenMon.Tests.Fakes {

    // The EC at port level, under the production EmbeddedControllerAbstract (its retry
    // and wait logic) and Hw's EC accessors, over a FakeEcRegisters register file. It
    // answers the ACPI EC protocol: a read command on the command port, the register on
    // the data port, then OutFull in the status byte and the value on the data port.
    // HandshakeFails simulates the issue #86 failure: the EC takes the command but never
    // sets OutFull, so every read attempt times out while the EC lock itself is obtained.
    // The lock is always obtained here (FakeEcRegisters.LockFails models a lock timeout).
    public sealed class FakeEmbeddedController : EmbeddedControllerAbstract {

        private enum State { Idle, ReadRegister, WriteRegister, WriteValue }

        private readonly FakeEcRegisters registers;
        private State state = State.Idle;
        private byte register;
        private byte output;
        private bool outFull;

        public bool HandshakeFails { get; set; }

        public FakeEmbeddedController(FakeEcRegisters registers) {
            this.registers = registers;
            this.IsInitialized = true;
        }

        public override void Initialize() {
            this.IsInitialized = true;
        }

        public override void Close() {
            this.IsInitialized = false;
        }

        public override bool Request(int timeout) {
            return true;
        }

        public override void Release() {
        }

        // Status on the command port, data on the data port
        protected override byte ReadIoPort(Port port) {
            if(port == Port.Command)
                return (byte) (this.outFull && !this.HandshakeFails ? Status.OutFull : 0);
            this.outFull = false;
            return this.output;
        }

        protected override void WriteIoPort(Port port, byte value) {
            if(port == Port.Command) {
                this.outFull = false;
                this.state = value == (byte) Command.Read ? State.ReadRegister
                    : value == (byte) Command.Write ? State.WriteRegister
                    : State.Idle;
                return;
            }
            switch(this.state) {
                case State.ReadRegister:
                    this.output = this.registers.Data[value];
                    this.outFull = true;
                    this.state = State.Idle;
                    break;
                case State.WriteRegister:
                    this.register = value;
                    this.state = State.WriteValue;
                    break;
                case State.WriteValue:
                    this.registers.Data[this.register] = value;
                    this.state = State.Idle;
                    break;
            }
        }

    }

}
