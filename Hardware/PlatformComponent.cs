  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using OmenMon.Core;
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

    // A component a BIOS-only model does not have (an EC fan register it must never touch:
    // switch, countdown, manual, mode, rate, level, speed). Reads report "unknown" (Unknown,
    // never a byte, and no valid reading); a write does nothing and is recorded once per
    // component (Refused), so a code path that still tries one shows up in -Diag instead
    // of reaching the EC.
    public sealed class AbsentComponent : PlatformComponentAbstract, IPlatformReadWriteComponent {

        // What GetValue() returns: not a value any register can hold
        public const int Unknown = -1;

        // Names of the absent components a write was refused for (one entry each)
        private static readonly System.Collections.Generic.List<string> refused =
            new System.Collections.Generic.List<string>();
        public static string[] Refused {
            get { lock(refused) return refused.ToArray(); }
        }

        private bool loggedWrite;

        public AbsentComponent(string name) {
            this.AccessType = PlatformData.AccessType.Read | PlatformData.AccessType.Write;
            this.Constraint = int.MaxValue;
            this.LinkType = PlatformData.LinkType.EmbeddedController;
            this.Size = PlatformData.DataSize.Byte;
            this.Name = name;
            this.LastValue = Unknown;
            this.PreviousValue = Unknown;
        }

        // Nothing to read: the value stays Unknown, and there is never a valid reading
        public override bool Update() {
            return false;
        }

        public override int GetValue() {
            return Unknown;
        }

        protected override int Read() {
            return Unknown;
        }

        protected override bool TryRead(out int value) {
            value = Unknown;
            return false;
        }

        // Nothing is written; the first refused write is recorded
        public override void SetValue(int value) {
            if(this.loggedWrite)
                return;
            this.loggedWrite = true;
            string entry = (this.Name ?? "?") + " (BIOS-only model, write of " + value + " refused)";
            lock(refused)
                if(!refused.Contains(entry))
                    refused.Add(entry);
            System.Diagnostics.Trace.WriteLine("OmenMon: " + entry);
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

    // A mode register verified to hold what BIOS 0x1A last sent (the model entry's
    // <ModeReg Verified="true">; 8BAC and 8C77: EC 0x59 OGHM, which the ACPI method behind
    // 0x1A writes verbatim). Read only: the mode is set through the BIOS, never the EC.
    // TryReadMode tells a failed read apart from a value (Update() does not).
    public sealed class VerifiedModeComponent : EcComponent {

        public VerifiedModeComponent(byte register, IEcRegisters registers = null)
            : base(register, PlatformData.AccessType.Read, PlatformData.DataSize.Byte, int.MaxValue, registers) {
            SetName("Mode");
        }

        public bool TryReadMode(out int value) {
            return TryReadRegister(out value);
        }

    }

    // A GPU temperature from the graphics driver. TryRead gives a temperature only while
    // the dGPU is awake (it must never wake a parked one); false otherwise or if the
    // driver cannot be read.
    public interface IGpuTemperatureSource {
        bool TryRead(out int celsius);
    }

    // The GPU temperature of a board without a GPU register (an unknown board running
    // BIOS-only): the graphics driver's reading while the dGPU is awake. While it is
    // parked, or the driver does not answer, there is no reading: after the GPU has read
    // once that is "unknown", which the fan-safety guard treats as hot (F3 as designed;
    // an EC GPU register reads 0 for a parked GPU, the driver gives nothing to tell the
    // two apart by).
    public class GpuDriverTemperatureComponent : PlatformComponentAbstract, IPlatformReadComponent {

        private readonly IGpuTemperatureSource source;

        public GpuDriverTemperatureComponent(IGpuTemperatureSource source, int constraint = int.MaxValue) {
            this.source = source;
            this.AccessType = PlatformData.AccessType.Read;
            this.Constraint = constraint;
            this.LinkType = PlatformData.LinkType.WmiBios;
            this.Size = PlatformData.DataSize.Byte;
            this.Name = "GPTM";
        }

        protected override int Read() {
            TryRead(out int value);
            return value;
        }

        protected override bool TryRead(out int value) {
            value = 0;
            try {
                if(this.source != null && this.source.TryRead(out int celsius)) {
                    value = celsius;
                    return true;
                }
            } catch { }
            return false;
        }

        // 0 is not a reading (a valid one is a plausible temperature)
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
