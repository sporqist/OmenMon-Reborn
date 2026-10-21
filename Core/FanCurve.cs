  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Collections.Generic;

namespace OmenMon.Core {

    // A fan program's table, evaluated: from each temperature threshold on, these levels
    // [rpm / 100] for the CPU and the GPU fan. The program itself (FanProgram in the
    // application) reads the sensors and writes the levels; this only decides them.
    public sealed class FanCurve {

        // Temperature thresholds in ascending order, and the levels for each
        private readonly List<byte> thresholds;
        private readonly IDictionary<byte, byte[]> levels;

        // thresholds: ascending (a SortedDictionary's keys); levels: the levels for each
        public FanCurve(IEnumerable<byte> thresholds, IDictionary<byte, byte[]> levels) {
            this.thresholds = new List<byte>(thresholds);
            this.levels = levels;
        }

        // The levels of the highest threshold
        public byte[] Top {
            get { return this.levels[this.thresholds[this.thresholds.Count - 1]]; }
        }

        // The levels for the given temperature
        public byte[] LevelAt(byte temperature) {
            return this.levels[Threshold(temperature)];
        }

        // The threshold whose levels apply at the given temperature: the highest one at
        // or below it, or the lowest one if the temperature is below all of them
        public byte Threshold(byte temperature) {
            int value;

            // Binary-search the threshold list
            // If the result is non-negative, an index was found
            if((value = this.thresholds.BinarySearch(temperature)) >= 0)

                // Return the item at the index directly
                return this.thresholds[value];

            // The result is a bitwise complement of the next larger item index,
            // or the index of the last element of the list if no larger item exists
            else {

                // Return the item at the binary complement index less one,
                // clamped to a minimum of 0 to avoid OutOfBoundsException if the
                // temperature is lower than the lowest defined threshold in the curve.
                int index = ~value - 1;
                return this.thresholds[index < 0 ? 0 : index];

            }

        }

        // Chooses the fan levels for the current readings. Each fan follows its own
        // component's temperature, except:
        //   * No fresh CPU reading (EC lock timeouts, WMI failure, implausible values for
        //     longer than the reading age limit, FanSafetyPolicy.ReadingMaxAge): the
        //     temperature is unknown, so both fans get the program's highest row. Unknown
        //     is treated as hot, never as cold — previously a failed read was 0 °C and
        //     selected the lowest row (possibly 0/0, fans off) and re-sent it every
        //     program tick.
        //   * GPU state unknown (it has read before, and now its reads fail or are
        //     implausible for longer than the reading age limit, with no 0 read either):
        //     the GPU fan gets the program's highest row, and so does the CPU fan when
        //     the fans share the temperature. Before, a failed GPU read was taken for a
        //     parked GPU, and the GPU fan followed the CPU while the GPU was hot.
        //   * GPU parked (reads 0) or never observed (a board without a GPU sensor):
        //     the GPU fan follows the CPU temperature. This replaces the issue #66 rule
        //     that left the GPU fan on the 0 °C row whenever the GPU had once been seen
        //     and then read 0 — on boards whose fans share heat pipes that idled the GPU
        //     fan under full CPU load.
        // cpu counts if it has a value (the caller passes a fresh one or none); gpuValue
        // counts only when gpuState is Fresh. sharedTemperature: both fans follow the
        // hotter chip (FanProgramSharedTemperature). safeLevel is true when a sensor is
        // unknown and the highest row was used for it.
        public byte[] Select(SensorReading cpu, GpuState gpuState, int gpuValue, bool sharedTemperature, out bool safeLevel) {

            byte[] top = Top;

            if(!cpu.HasValue) {
                safeLevel = true;
                return new byte[] { top[0], top[1] };
            }

            byte cpuTemp = ClampTemperature(cpu.Value);

            if(gpuState == GpuState.Unknown) {
                safeLevel = true;
                if(sharedTemperature)
                    return new byte[] { top[0], top[1] };
                return new byte[] { LevelAt(cpuTemp)[0], top[1] };
            }

            safeLevel = false;
            byte gpuTemp = gpuState == GpuState.Fresh ? ClampTemperature(gpuValue) : cpuTemp;

            // Both fans follow the hotter chip when configured to share it
            // (common heat pipes, e.g. 8BAC); otherwise each fan follows its own
            if(sharedTemperature)
                cpuTemp = gpuTemp = Math.Max(cpuTemp, gpuTemp);

            // CPU fan speed from the CPU-temperature row,
            // GPU fan speed from the GPU-temperature row
            byte[] cpuFans = LevelAt(cpuTemp);
            byte[] gpuFans = LevelAt(gpuTemp);
            return new byte[] { cpuFans[0], gpuFans[1] };

        }

        private static byte ClampTemperature(int value) {
            return (byte) Math.Max(0, Math.Min(byte.MaxValue, value));
        }

    }

}
