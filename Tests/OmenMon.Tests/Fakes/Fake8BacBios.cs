  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Hardware.Bios;

namespace OmenMon.Tests.Fakes {

    // Simulates the BIOS WMI interface of an HP OMEN 16-wf0xxx (board 8BAC, BIOS F.30)
    // as far as fan control goes. The rules are the measured ones:
    //
    //   * 0x2E [cpu, gpu] sets the fan levels (rpm / 100), but only takes effect when a
    //     0x10 fan-count query came before it, at most ArmWindow earlier (measured: at
    //     least 30 s). A 0x2E without that is accepted (rc 0) and silently ignored.
    //   * 0x10 on its own also keeps an active level alive.
    //   * Without refresh (0x2E or 0x10), an active level expires ExpiryAfter (~124 s)
    //     after the last refresh and the BIOS curve resumes.
    //   * 0/0 means both fans OFF, not automatic.
    //   * There is no release command; 255/255 keeps the current speed.
    //   * 0x2D reports the measured fan levels, 0x23 one temperature (CPU).
    //   * 0x27 (Max Fan) and 0x32 (fan table write) are accepted and ignored.
    //
    // Time only moves when the test moves it (Advance / Now).
    public sealed class Fake8BacBios : IBiosTransport {

        public DateTime Now { get; set; } = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        public TimeSpan ArmWindow { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan ExpiryAfter { get; set; } = TimeSpan.FromSeconds(124);

        // What the BIOS's own curve drives the fans at when no level is active
        public byte[] CurveLevels { get; set; } = new byte[] { 24, 26 };

        // Reported by 0x23
        public byte CpuTemperature { get; set; } = 45;

        // When set, every call fails client-side (rc -1), like a broken WMI session
        public bool FailAll { get; set; }

        // Every call, in order: (command type, input payload)
        public List<(uint Type, byte[] Data)> Calls { get; } = new List<(uint, byte[])>();

        public BiosData.FanMode? LastFanMode { get; private set; }

        private DateTime? lastQueryAt;
        private byte[]? active;
        private DateTime activeRefreshedAt;

        public void Advance(TimeSpan span) {
            Now += span;
        }

        // The level the BIOS is applying right now (null: its own curve)
        public byte[]? ActiveLevels {
            get {
                Expire();
                return this.active == null ? null : (byte[]) this.active.Clone();
            }
        }

        // What the fans are actually doing right now
        public byte[] MeasuredLevels {
            get {
                Expire();
                return (byte[]) (this.active ?? this.CurveLevels).Clone();
            }
        }

        public bool FansOff {
            get {
                byte[] m = MeasuredLevels;
                return m[0] == 0 && m[1] == 0;
            }
        }

        public int Count(uint type) {
            return this.Calls.Count(c => c.Type == type);
        }

        // Fan-level writes (0x2E) in order
        public List<byte[]> LevelWrites {
            get { return this.Calls.Where(c => c.Type == 0x2E).Select(c => new byte[] { c.Data[0], c.Data[1] }).ToList(); }
        }

        private void Expire() {
            if(this.active != null && this.Now - this.activeRefreshedAt >= this.ExpiryAfter)
                this.active = null;
        }

        public int Send(BiosData.Cmd command, uint commandType, byte[]? inData, byte outDataSize, out byte[] outData) {

            byte[] data = inData == null ? new byte[4] : (byte[]) inData.Clone();
            this.Calls.Add((commandType, data));
            outData = new byte[outDataSize];

            if(this.FailAll)
                return -1;

            if(command != BiosData.Cmd.Default)
                return 0;

            Expire();

            switch(commandType) {

                case 0x10: // Fan count; arms 0x2E and refreshes an active level
                    this.lastQueryAt = this.Now;
                    if(this.active != null)
                        this.activeRefreshedAt = this.Now;
                    outData[0] = 2;
                    return 0;

                case 0x2E: // Set fan level
                    if(data[0] == 0xFF && data[1] == 0xFF)
                        return 0; // keeps the current speed
                    if(this.lastQueryAt.HasValue && this.Now - this.lastQueryAt.Value <= this.ArmWindow) {
                        this.active = new byte[] { data[0], data[1] };
                        this.activeRefreshedAt = this.Now;
                    }
                    return 0;

                case 0x2D: // Measured fan levels
                    byte[] m = this.active ?? this.CurveLevels;
                    outData[0] = m[0];
                    outData[1] = m[1];
                    return 0;

                case 0x23: // Temperature (CPU)
                    outData[0] = this.CpuTemperature;
                    return 0;

                case 0x1A: // Fan mode
                    this.LastFanMode = (BiosData.FanMode) data[1];
                    return 0;

                case 0x26: // Max Fan status: never on
                    outData[0] = 0;
                    return 0;

                case 0x2F: // Fan table: 2 fans, 3 rows (cpu, gpu, temperature)
                    byte[] table = { 2, 3, 20, 22, 40, 35, 37, 60, 52, 55, 80 };
                    Array.Copy(table, outData, Math.Min(table.Length, outData.Length));
                    return 0;

                case 0x27: // Max Fan: ignored on 8BAC
                case 0x32: // Fan table write: ignored on 8BAC
                default:
                    return 0;

            }

        }

    }

}
