  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;

namespace OmenMon.Core {

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

}
