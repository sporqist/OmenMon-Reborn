  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Collections.Generic;

namespace OmenMon.Core {

    // Combining sensor readings into the temperatures fan control decides on. The
    // platform (in the application) reads the sensors and passes their readings in.
    public static class SensorReadings {

        // The hotter of two readings; a missing one does not count (a tie keeps a)
        public static SensorReading Hotter(SensorReading a, SensorReading b) {
            if(!a.HasValue) return b;
            if(!b.HasValue) return a;
            return b.Value > a.Value ? b : a;
        }

        // The highest fresh reading among several (the first of equal ones), or
        // SensorReading.None if none is fresh
        public static SensorReading HottestFresh(IEnumerable<SensorReading> readings, DateTime now, TimeSpan maxAge) {
            SensorReading best = SensorReading.None;
            foreach(SensorReading r in readings)
                if(r.IsFresh(now, maxAge) && (!best.HasValue || r.Value > best.Value))
                    best = r;
            return best;
        }

        // The hottest temperature of the system: the CPU reading (already judged fresh,
        // or None), the GPU if its reading is fresh, and every other fresh reading that
        // counts for the maximum temperature.
        //
        // complete = false when the true hottest temperature may be higher than the
        // result: the CPU does not read, or the GPU has read before and its state is now
        // unknown (failed reads, not a parked GPU). Thermal panic may start on an
        // incomplete reading, never end on it. A GPU at 97 °C must start panic even
        // while the CPU does not read (Marius, 2026-10-21). None only if nothing reads.
        public static SensorReading Hottest(
            SensorReading cpu,
            GpuReading gpu,
            IEnumerable<SensorReading> others,
            DateTime now,
            TimeSpan maxAge,
            out bool complete) {

            complete = true;
            SensorReading hottest = cpu;
            if(!hottest.HasValue)
                complete = false;
            switch(gpu.StateAt(now, maxAge)) {
                case GpuState.Unknown:
                    complete = false;
                    break;
                case GpuState.Fresh:
                    hottest = Hotter(hottest, gpu.Last);
                    break;
            }
            foreach(SensorReading r in others)
                if(r.IsFresh(now, maxAge))
                    hottest = Hotter(hottest, r);
            return hottest;

        }

    }

}
