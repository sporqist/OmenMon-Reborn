  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using OmenMon.Hardware;
using OmenMon.Hardware.Ec;
using OmenMon.Library;

namespace OmenMon.Hardware.Platform {

    // Time source for sensor readings (UTC). Production uses the system clock;
    // tests substitute a simulated one.
    public static class PlatformClock {

        internal static Func<DateTime> Source = () => DateTime.UtcNow;

        public static DateTime UtcNow { get { return Source(); } }

    }

    // The last valid value of a sensor and when it was taken. A failed or implausible
    // read never produces a reading, so "no reading" can never be mistaken for 0 °C.
    // Callers decide how old a reading may be (IsFresh) before they stop trusting it.
    public readonly struct SensorReading {

        public readonly int Value;
        public readonly DateTime TakenAt;  // UTC; default(DateTime) when there is none

        public SensorReading(int value, DateTime takenAt) {
            this.Value = value;
            this.TakenAt = takenAt;
        }

        public static readonly SensorReading None = default(SensorReading);

        public bool HasValue { get { return this.TakenAt != default(DateTime); } }

        // True if there is a reading and it is no older than maxAge at the given time
        public bool IsFresh(DateTime now, TimeSpan maxAge) {
            return this.HasValue && now - this.TakenAt <= maxAge;
        }

        public override string ToString() {
            return this.HasValue ? this.Value + " @ " + this.TakenAt.ToString("HH:mm:ss") : "none";
        }

    }

    // What the GPU temperature sensor says about the GPU at a given time
    public enum GpuState {
        Fresh,    // a valid reading within the age limit
        Parked,   // reads 0 (dGPU powered off), or has never given a valid reading
                  // (a board without a GPU sensor, or a GPU parked since start)
        Unknown   // has read before, but neither a valid reading nor a 0 is recent:
                  // the reads fail (EC lock timeouts, a failed EC handshake) or are
                  // implausible
    }

    // The recent history of the GPU temperature sensor. The EC GPU register (8BAC: 0xB4)
    // reads 0 while the dGPU is parked, and a failed EC read gives 0 as well, so the two
    // are told apart by whether the read itself succeeded: a parked GPU is not a heat
    // source, a GPU whose temperature is unknown counts as hot.
    public readonly struct GpuReading {

        public readonly SensorReading Last;  // last valid reading (None: there never was one)
        public readonly DateTime ZeroAt;     // last read that succeeded with 0 (UTC; default: never)

        public GpuReading(SensorReading last, DateTime zeroAt) {
            this.Last = last;
            this.ZeroAt = zeroAt;
        }

        // No GPU sensor, or one that has never read
        public static readonly GpuReading None = default(GpuReading);

        public GpuState StateAt(DateTime now, TimeSpan maxAge) {

            // A fresh valid reading wins over a later 0: a single 0 can be a glitch, and a
            // hot GPU must not drop out of the judgement for it. A GPU that really parked
            // counts at its last value until that goes stale, as it always has.
            if(this.Last.IsFresh(now, maxAge))
                return GpuState.Fresh;

            // Never observed: judged on the CPU alone, as before (boards without a GPU
            // sensor, or a GPU parked since start)
            if(!this.Last.HasValue)
                return GpuState.Parked;

            if(this.ZeroAt != default(DateTime) && now - this.ZeroAt <= maxAge)
                return GpuState.Parked;

            return GpuState.Unknown;

        }

        public override string ToString() {
            return this.Last + (this.ZeroAt != default(DateTime) ? ", 0 @ " + this.ZeroAt.ToString("HH:mm:ss") : "");
        }

    }

    // Defines a common interface for all platform components
    public interface IPlatformComponent {

        // Retrieves the access type
        public PlatformData.AccessType GetAccessType();

        // Retrieves the data size
        public PlatformData.DataSize GetDataSize();

        // Retrieves the link type
        public PlatformData.LinkType GetLinkType();

        // Retrieves the name
        public string GetName();

        // Sets the name
        public void SetName(string name);

    }

    // Defines an interface for interacting with a readable component
    public interface IPlatformReadComponent : IPlatformComponent {

        // Retrieves the current constraint value
        public int GetConstraint();

        // Retrieves the current sensor value
        public int GetValue();

        // Retrieves the value trend
        public PlatformData.ValueTrend GetValueTrend();

        // Sets the constraint value
        public void SetConstraint(int constraint);

        // Updates the sensor value
        public bool Update();

        // Retrieves the last valid value and when it was read
        // (SensorReading.None if no read has ever been valid)
        public SensorReading GetReading();

        // Retrieves when a read last succeeded with the value 0 (UTC; default(DateTime)
        // if never). Only EC components report a failed read as such (TryRead); for the
        // others a failed read that returns 0 counts here too.
        public DateTime GetZeroReadAt();

    }

    // Defines an interface for interacting with a writeable component
    public interface IPlatformWriteComponent : IPlatformComponent {

        // Sets the component value
        public void SetValue(int value);

    }

    // Defines an interface for interacting with
    // a component that is both readable and writeable
    public interface IPlatformReadWriteComponent :
        IPlatformReadComponent, IPlatformWriteComponent {

    }

    // Provides common base for all kinds of components
    public abstract class PlatformComponentAbstract : IPlatformReadWriteComponent {

        // Stores the access type
        protected PlatformData.AccessType AccessType;

        // Stores the constraint value
        protected int Constraint;

        // Stores the component name
        protected string Name;

        // Stores the data size
        protected PlatformData.DataSize Size;

        // Stores the linktype
        protected PlatformData.LinkType LinkType;

        // Constructs a component instance
        public PlatformComponentAbstract(
            PlatformData.AccessType access = PlatformData.AccessType.Read) {

            // Set the access type
            this.AccessType = access;
        }

        // Retrieves the access type
        public PlatformData.AccessType GetAccessType() {
            return this.AccessType;
        }

        // Retrieves the constraint value
        public int GetConstraint() {
            return this.Constraint;
        }

        // Retrieves the data size
        public PlatformData.DataSize GetDataSize() {
            return this.Size;
        }

        // Retrieves the link type
        public PlatformData.LinkType GetLinkType() {
            return this.LinkType;
        }

        // Retrieves the component name
        public virtual string GetName() {
            return this.Name;
        }

        // Sets the constraint value
        public void SetConstraint(int constraint) {
            this.Constraint = constraint;
        }

        // Sets the component name
        public virtual void SetName(string name) {
            this.Name = name;
        }

        // Reading operations

        // Stores the last and previous values
        protected int LastValue;
        protected int PreviousValue;

        // Last read that succeeded and passed IsValidValue(), and when it happened.
        // Unlike LastValue (what the UI shows, which drops to 0 after repeated zero
        // reads for legacy reasons), this never changes on a failed or implausible read.
        protected SensorReading LastReading;

        // When a read last succeeded with 0 (a parked GPU, as opposed to a failed read)
        protected DateTime LastZeroAt;

        // Retrieves the last valid reading
        public virtual SensorReading GetReading() {
            AssertHasAccess(PlatformData.AccessType.Read);
            return this.LastReading;
        }

        // Retrieves when a read last succeeded with 0
        public virtual DateTime GetZeroReadAt() {
            AssertHasAccess(PlatformData.AccessType.Read);
            return this.LastZeroAt;
        }

        // Whether a successfully read value is a real measurement. By default anything
        // within the constraint is; temperature sensors are stricter (0 is not a reading).
        protected virtual bool IsValidValue(int value) {
            return value <= this.Constraint;
        }

        // Checks whether a read or write operation is valid for the component
        protected virtual void AssertHasAccess(PlatformData.AccessType access) {
            if(!this.AccessType.HasFlag(access))
                throw new InvalidOperationException();
        }

        // Retrieves the current component value
        public virtual int GetValue() {
            // Ensure the component can be read from
            AssertHasAccess(PlatformData.AccessType.Read);
            return this.LastValue;
        }

        // Retrieves the component value trend
        public virtual PlatformData.ValueTrend GetValueTrend() {
            // Ensure the component can be read from
            AssertHasAccess(PlatformData.AccessType.Read);
            return LastValue != PreviousValue ?
                LastValue > PreviousValue ?
                    PlatformData.ValueTrend.Ascending
                    : PlatformData.ValueTrend.Descending
                    : PlatformData.ValueTrend.Unchanged;
        }

        // Updates the component value
        public virtual bool Update() {
            // Ensure the component can be read from
            AssertHasAccess(PlatformData.AccessType.Read);

            try {

                // Read the value, noting whether the read itself succeeded
                bool readOk = TryRead(out int value);

                // Record a valid reading regardless of the display logic below,
                // and a successful 0 apart from a failed read
                if(readOk && IsValidValue(value))
                    this.LastReading = new SensorReading(value, PlatformClock.UtcNow);
                else if(readOk && value == 0)
                    this.LastZeroAt = PlatformClock.UtcNow;

                // Hold off on one additional time
                // for values that might be intermittently zeroed
                if(value == 0 && this.PreviousValue != 0) {
                    this.PreviousValue = 0;
                    return false;
                }

                // Only update if the reading
                // is not obviously incorrect
                if(value <= this.Constraint) {

                    // Update the previous value
                    this.PreviousValue = this.LastValue;
                    this.LastValue = value;

                    // Update succeeded
                    return true;

                }

            } catch { }

                // Update failed
                return false;

        }

        // Writing operations

        // Sets the current component value
        public virtual void SetValue(int value) {
            // Ensure the component can be written to
            AssertHasAccess(PlatformData.AccessType.Write);

            // Set the value
            Write(value);

            // If the component can also be read from, update the value
            if(this.AccessType.HasFlag(PlatformData.AccessType.Read))
                Update();

        }

        // Implements component value retrieval
        protected abstract int Read();

        // Implements component value retrieval, reporting whether the read succeeded.
        // A failed read yields the same value Read() returns for it (0 for the EC on a
        // lock timeout), so the display logic in Update() is unchanged.
        protected virtual bool TryRead(out int value) {
            value = Read();
            return true;
        }

        // Required due to inheritance, and implemented here so that
        // it does not have to be repeated in every derived class,
        // even if the class does not implement the write interface
        protected virtual void Write(int value) {
            return;
        }

    }

    // Implements an Embedded Controller component
    public class EcComponent : PlatformComponentAbstract, IPlatformReadWriteComponent {

        // Stores the Embedded Controller register associated with the sensor
        protected byte Register;

        // Register access (the live EC through Hw unless a test substitutes one)
        protected IEcRegisters Registers;

        // Constructs an Embedded Controller readable component instance
        public EcComponent(
            byte register,
            PlatformData.AccessType access = PlatformData.AccessType.Read,
            PlatformData.DataSize size = PlatformData.DataSize.Byte,
            int constraint = int.MaxValue,
            IEcRegisters registers = null) {

            this.Registers = registers ?? HwEcRegisters.Instance;
            this.AccessType = access;
            this.Constraint = constraint;
            this.LinkType = PlatformData.LinkType.EmbeddedController;
            this.Register = register;
            this.Size = size;

            SetName();

        }

        // Defines a constructor for read-only non byte-sized data
        public EcComponent(byte register, PlatformData.DataSize size)
            : this(register, PlatformData.AccessType.Read, size) {

        }

        // Defines a constructor for read-only constrained-value data
        public EcComponent(byte register, int constraint)
            : this(register, PlatformData.AccessType.Read, PlatformData.DataSize.Byte, constraint) {

        }

        // Sets the name of an Embedded Controller sensor
        public override void SetName(string name = "") {
            if(name != "")
                this.Name = name;
            else try {
                // Use the DSDT table entry as the name
                this.Name = Enum.GetName(typeof(EmbeddedControllerData.Register), this.Register);
            } catch {
                // Set a generic name based on the register number
                this.Name = "R" + Conv.GetString(this.Register, 3, 10);
            }
        }

        // Reads a value from the Embedded Controller, reporting whether the EC lock was
        // obtained (and, for a byte, whether the EC handshake succeeded). On failure the
        // value is 0, which is what Hw.EcGetByte/EcGetWord return for it.
        protected bool TryReadRegister(out int value) {
            bool ok;
            if(this.Size == PlatformData.DataSize.Byte) {
                ok = this.Registers.TryReadByte(this.Register, out byte b);
                value = b;
            } else {
                ok = this.Registers.TryReadWord(this.Register, out ushort w);
                value = w;
            }
            return ok;
        }

        // Reads a value from the Embedded Controller
        protected override int Read() {
            TryReadRegister(out int value);
            return value;
        }

        // Reads a value from the Embedded Controller, reporting a failed read
        protected override bool TryRead(out int value) {
            return TryReadRegister(out value);
        }

        // Writes a value to the Embedded Controller
        protected override void Write(int value) {
            if(this.Size == PlatformData.DataSize.Byte)
                this.Registers.TryWriteByte(this.Register, (byte) value);
            else
                this.Registers.TryWriteWord(this.Register, (ushort) value);
        }

    }

    // Implements an Embedded Controller temperature sensor. A reading is only valid if the
    // EC lock was obtained and the value is a plausible temperature: 0 is what a failed
    // read, a parked GPU and an unpopulated register all return, so it is never treated
    // as a measurement; values above the constraint (0xFF and similar firmware bytes)
    // are not either.
    public class EcTemperatureComponent : EcComponent {

        public EcTemperatureComponent(byte register, int constraint, IEcRegisters registers = null)
            : base(register, PlatformData.AccessType.Read, PlatformData.DataSize.Byte, constraint, registers) {
        }

        protected override bool IsValidValue(int value) {
            return value > 0 && value <= this.Constraint;
        }

    }

    // Implements a BIOS Temperature component
    public class WmiBiosTemperatureComponent : PlatformComponentAbstract, IPlatformReadComponent {

        // BIOS used for the reading (the shared Hw.Bios unless a test substitutes one)
        private readonly OmenMon.Hardware.Bios.IBiosCtl bios;

        // Constructs an BIOS Temperature readable component instance
        public WmiBiosTemperatureComponent(int constraint = int.MaxValue,
            OmenMon.Hardware.Bios.IBiosCtl bios = null) {

            this.bios = bios;
            this.AccessType = PlatformData.AccessType.Read;
            this.Constraint = constraint;
            this.LinkType = PlatformData.LinkType.WmiBios;
            this.Size = PlatformData.DataSize.Byte;

            SetName();

        }

        // Sets the name of an Embedded Controller sensor
        public override void SetName(string name = "") {
            if(name != "")
                this.Name = name;
            else
                this.Name = "BIOS";
        }

        // Reads a temperature value from the BIOS
        protected override int Read() {
            return Hw.BiosGet((this.bios ?? Hw.Bios).GetTemperature);
        }

        // A failed BIOS call reads as 0 (the output buffer stays zeroed), so as for the
        // EC sensors 0 is not a reading
        protected override bool IsValidValue(int value) {
            return value > 0 && value <= this.Constraint;
        }

    }

}
