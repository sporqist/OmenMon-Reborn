  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Core;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // F2: a BIOS-only model reads its temperatures from BIOS 0x23 and the EC registers it
    // names, never from the global <Temperature> sensors (CPUT 0x57, GPTM 0xB7, RTMP 0x58,
    // TMP1 0x59, TNTx), which are other data on these boards
    [Collection(HardwareStateCollection.Name)]
    public class BiosOnlyTemperatureTests : IDisposable {

        private readonly Dictionary<string, Config.TemperatureSensorData> savedSensors = Config.TemperatureSensor;
        private readonly int savedProgramInterval = Config.UpdateProgramInterval;
        private readonly Dictionary<string, PlatformPreset> savedModels = Config.Models;

        public BiosOnlyTemperatureTests() {
            // The shipped sensor list, RTMP and TMP1 counting for the maximum (as on a
            // fresh install)
            Config.TemperatureSensor = new Dictionary<string, Config.TemperatureSensorData> {
                ["CPUT"] = new Config.TemperatureSensorData(PlatformData.LinkType.EmbeddedController, 0x57),
                ["GPTM"] = new Config.TemperatureSensorData(PlatformData.LinkType.EmbeddedController, 0xB7),
                ["BIOS"] = new Config.TemperatureSensorData(PlatformData.LinkType.WmiBios, true),
                ["RTMP"] = new Config.TemperatureSensorData(PlatformData.LinkType.EmbeddedController, 0x58),
                ["TMP1"] = new Config.TemperatureSensorData(PlatformData.LinkType.EmbeddedController, 0x59) };
        }

        public void Dispose() {
            Config.TemperatureSensor = this.savedSensors;
            Config.UpdateProgramInterval = this.savedProgramInterval;
            Config.Models = this.savedModels;
            Config.FanProgram.Remove(BiosOnlySessionTests.ProgramName);
        }

        // What the fan logic decided at one step
        private sealed record Step(byte Max, int Hottest, bool Complete, ThermalPanicTransition Panic, string Writes);

        // Runs a warm-up into thermal panic and back with a fan program; returns what the
        // fan logic saw and wrote at each step
        private static List<Step> Run(bool junk) {
            Config.UpdateProgramInterval = 5;
            using var rig = new TestRig(preset: BiosOnlySessionTests.Board8C77());
            BiosOnlySessionTests.Fill8C77Image(rig.Ec, 0x00);
            if(junk) {
                rig.Ec.Data[0x57] = 0x40;   // keyboard WASD blue: "64 °C" as CPUT
                rig.Ec.Data[0x58] = 0x43;   // flag bits: "67 °C" as RTMP
                rig.Ec.Data[0x59] = 0x50;   // Cool mode in OGHM: "80 °C" as TMP1
                rig.Ec.Data[0xB7] = 0x5A;   // a board sensor, not the GPU: "90 °C" as GPTM
            } else {
                rig.Ec.Data[0x57] = rig.Ec.Data[0x58] = rig.Ec.Data[0x59] = rig.Ec.Data[0xB7] = 0;
            }
            TestRig.AddProgram(BiosOnlySessionTests.ProgramName, (0, 20, 22), (60, 35, 35), (80, 50, 50));
            var session = new GuiSession(rig) { PanicThreshold = 90, PanicHysteresis = 5 };
            session.AutoConfig(BiosOnlySessionTests.ProgramName);

            var steps = new List<Step>();
            foreach(byte cpu in new byte[] { 45, 55, 66, 78, 86, 92, 95, 89, 84, 70, 50, 42 }) {
                rig.SetCpuTemperature(cpu);
                rig.Ec.GpuTemperature = (byte) (cpu > 60 ? cpu - 8 : 0);
                int from = rig.Bios.Calls.Count;
                session.Pass(true);
                SensorReading hottest = rig.Platform.GetHottestReading(false, out bool complete);
                ThermalPanicTransition panic = session.Panic.Check(rig.Platform, true, 90, 5, out _);
                string writes = string.Join(" ", rig.Bios.Calls.Skip(from).Where(c => c.Type == 0x2E)
                    .Select(c => c.Data[0] + "/" + c.Data[1]));
                steps.Add(new Step(rig.Platform.GetMaxTemperature(false), hottest.Value, complete,
                    session.Panic.IsActive ? ThermalPanicTransition.Entered : panic, writes));
                rig.Advance(15);
            }
            Assert.Empty(rig.Ec.Reads.Except(new[] { FakeEcRegisters.CpuTempRegister, FakeEcRegisters.GpuTempRegister }));
            return steps;
        }

        // T5: junk in the global sensors' registers changes neither the maximum
        // temperature, nor panic, nor a program's levels
        [Fact]
        public void BiosOnly_GlobalSensorJunk_ChangesNothing() {
            List<Step> clean = Run(false);
            List<Step> junk = Run(true);
            Assert.Equal(clean, junk);

            // The run did go through panic and back, and the program wrote levels
            Assert.Contains(clean, s => s.Panic == ThermalPanicTransition.Entered);
            Assert.Equal(95, clean.Max(s => s.Max));
            Assert.Equal(42, clean.Last().Max);
            Assert.All(clean, s => Assert.NotEmpty(s.Writes));
        }

        // The sensors a BIOS-only model gets: its own registers and BIOS 0x23, all
        // counting for the maximum
        [Fact]
        public void BiosOnly_Sensors_AreTheModelsOwn() {
            using var rig = new TestRig(preset: BiosOnlySessionTests.Board8C77());
            Assert.Equal(new[] { "CPUT", "GPTM", "BIOS" }, rig.Platform.Temperature.Select(t => t.GetName()));
            Assert.All(rig.Platform.TemperatureUse, Assert.True);

            var noRegisters = new PlatformPreset { ProductId = "XXXX", FanControl = FanControlKind.Bios };
            IPlatformReadComponent[] sensors = Platform.CreateTemperatures(noRegisters, out bool[] use, rig.Ec, rig.BiosCtl);
            Assert.Equal(new[] { "BIOS" }, sensors.Select(t => t.GetName()));
            Assert.Equal(new[] { true }, use);

            Assert.Equal(new[] { "CPUT (Ec 0xB2)", "GPTM (Ec 0xB4)", "BIOS (Bios)" },
                BiosFanControl.Sensors(0xB2, 0xB4).Select(p => p.ToString()));
            Assert.Equal(new[] { "BIOS (Bios)" }, BiosFanControl.Sensors(0, 0).Select(p => p.ToString()));
        }

        // Control: the same junk on an EC model's configured sensors is read as
        // temperatures (TMP1 0x59 = 0x50 shows as 80 °C), which is what F2 avoids
        [Fact]
        public void EcModel_GlobalSensorJunk_IsReadAsTemperature() {
            using var rig = new TestRig();
            rig.Ec.Data[0x59] = 0x50;
            rig.SetCpuTemperature(45);
            IPlatformReadComponent[] sensors = Platform.CreateTemperatures(PlatformPreset.Default, out bool[] use, rig.Ec, rig.BiosCtl);
            var platform = new Platform(rig.Settings, rig.Fans, sensors, use);
            platform.UpdateTemperature();
            Assert.Equal(80, platform.GetMaxTemperature(false));
        }

        // The GPU register the CLI reads for a model: a BIOS-only model's own or none,
        // never the global GPTM
        [Fact]
        public void GpuTemperatureRegister_BiosOnlyModel() {
            Config.Models = new Dictionary<string, PlatformPreset>(StringComparer.OrdinalIgnoreCase) {
                ["B1"] = new PlatformPreset { ProductId = "B1", FanControl = FanControlKind.Bios, TempGpuReg = 0xB4 },
                ["B2"] = new PlatformPreset { ProductId = "B2", FanControl = FanControlKind.Bios } };
            Assert.Equal(0xB4, Platform.GpuTemperatureRegister("B1"));
            Assert.Equal(0, Platform.GpuTemperatureRegister("B2"));
        }

    }

}
