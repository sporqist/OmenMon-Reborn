  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System.Text;
using OmenMon.Core;
using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // F3: a board without a model entry runs BIOS-only when its BIOS fan interface
    // answers, and the heuristic layout detection never suggests a control register
    [Collection(HardwareStateCollection.Name)]
    public class BiosOnlyUnknownBoardTests : IDisposable {

        private readonly Dictionary<string, PlatformPreset> savedModels = Config.Models;

        public BiosOnlyUnknownBoardTests() {
            Config.Models = new Dictionary<string, PlatformPreset>(StringComparer.OrdinalIgnoreCase) {
                ["8BAC"] = new PlatformPreset { ProductId = "8BAC", FanControl = FanControlKind.Bios } };
        }

        public void Dispose() {
            Config.Models = this.savedModels;
        }

        private static Platform.BiosFanProbe Answers() {
            return new Platform.BiosFanProbe { FanCount = 2, Levels = new byte[] { 20, 23 }, ThermalPolicyV1 = true };
        }

        [Fact]
        public void Decide_BiosOnlyWhenTheBiosFanInterfaceAnswers() {
            Assert.Equal(FanControlKind.Bios, UnknownBoard.Decide(2, new byte[] { 20, 23 }, true));
            Assert.Equal(FanControlKind.Bios, UnknownBoard.Decide(1, new byte[] { 0, 0 }, true));
            Assert.Equal(FanControlKind.Ec, UnknownBoard.Decide(0, new byte[] { 20, 23 }, true));
            Assert.Equal(FanControlKind.Ec, UnknownBoard.Decide(null, new byte[] { 20, 23 }, true));
            Assert.Equal(FanControlKind.Ec, UnknownBoard.Decide(2, null, true));
            Assert.Equal(FanControlKind.Ec, UnknownBoard.Decide(2, new byte[] { 20, 23 }, false));
        }

        [Fact]
        public void ResolvePreset_KnownUnknownAndLegacy() {
            int probes = 0;
            PlatformPreset known = Platform.ResolvePreset("8BAC", () => { probes++; return Answers(); }, out bool unverified);
            Assert.Same(Config.Models["8BAC"], known);
            Assert.False(unverified);
            Assert.Equal(0, probes);   // a known board is not probed

            PlatformPreset unknown = Platform.ResolvePreset("9ZZZ", Answers, out unverified);
            Assert.True(unknown.IsBiosOnly);
            Assert.True(unknown.TempGpuFromDriver);
            Assert.Equal(0, unknown.TempCpuReg);
            Assert.Equal(0, unknown.TempGpuReg);
            Assert.Equal("9ZZZ", unknown.ProductId);
            Assert.False(unverified);

            PlatformPreset legacy = Platform.ResolvePreset("9ZZZ", () => new Platform.BiosFanProbe(), out unverified);
            Assert.Same(PlatformPreset.Default, legacy);
            Assert.True(unverified);

            PlatformPreset failing = Platform.ResolvePreset("9ZZZ", () => throw new InvalidOperationException(), out unverified);
            Assert.Same(PlatformPreset.Default, failing);
            Assert.True(unverified);
        }

        // An unknown board running BIOS-only: CPU from BIOS 0x23, GPU from the driver
        // while awake (unknown once it has read and then gives nothing)
        [Fact]
        public void UnknownBiosBoard_Sensors() {
            PlatformPreset preset = Platform.ResolvePreset("9ZZZ", Answers, out _);
            using var rig = new TestRig(preset: preset);
            Assert.Equal(new[] { "GPTM", "BIOS" }, rig.Platform.Temperature.Select(s => s.GetName()));
            Assert.All(rig.Platform.TemperatureUse, Assert.True);

            rig.GpuDriver.Celsius = 61;
            rig.SetCpuTemperature(50);
            rig.Platform.UpdateTemperature();
            Assert.Equal(61, rig.Platform.GetGpuReading().Last.Value);
            Assert.Equal(GpuState.Fresh, rig.Platform.GetGpuReading().StateAt(rig.Bios.Now, rig.Platform.ReadingMaxAge));
            Assert.Equal(50, rig.Platform.GetCpuReading().Value);
            Assert.Equal(61, rig.Platform.GetMaxTemperature(false));
            Assert.Equal(61, rig.Fans.Guard!.Hottest());

            // Parked: no reading; once the last one is stale the GPU is unknown, which
            // the guard treats as hot (no "hottest", not cool)
            rig.GpuDriver.Celsius = null;
            rig.Advance(60);
            rig.Platform.UpdateTemperature();
            Assert.Equal(GpuState.Unknown, rig.Platform.GetGpuReading().StateAt(rig.Bios.Now, rig.Platform.ReadingMaxAge));
            Assert.Null(rig.Fans.Guard!.Hottest());
            Assert.False(rig.Fans.Guard!.IsCool());
        }

        // A whole GUI session on an unknown BIOS-only board: the EC is neither written
        // nor read
        [Fact]
        public void UnknownBiosBoard_GuiSession_NoEcAccess() {
            PlatformPreset preset = Platform.ResolvePreset("9ZZZ", Answers, out _);
            using var rig = new TestRig(preset: preset);
            BiosOnlySessionTests.Fill8C77Image(rig.Ec, 0x02);
            rig.SetCpuTemperature(45);
            byte[] image = (byte[]) rig.Ec.Data.Clone();
            rig.Ec.Reads.Clear();
            TestRig.AddProgram(BiosOnlySessionTests.ProgramName, (0, 20, 22), (60, 35, 35));
            try {
                var session = new GuiSession(rig);
                session.AutoConfig(BiosOnlySessionTests.ProgramName);
                session.Run(30);
                session.TrayFanOff(true);
                session.Run(3);
                session.TrayFanOff(false);
                session.Set(FanActions.Request.Constant, level0: 30, level1: 33);
                session.Run(10);
                session.Exit();
            } finally {
                Config.FanProgram.Remove(BiosOnlySessionTests.ProgramName);
            }
            Assert.Empty(rig.Ec.Writes);
            Assert.Equal(image, rig.Ec.Data);
            Assert.Empty(rig.Ec.Reads);
            Assert.True(rig.Bios.Count(0x2E) > 0);
        }

        [Fact]
        public void PrintableRun_FindsStringsAroundARegister() {
            byte[] image = new byte[256];
            Assert.False(PrintableRun.Touches(image, 0x57));
            image[0x57] = 45;                                  // a temperature on its own
            Assert.False(PrintableRun.Touches(image, 0x57));
            Encoding.ASCII.GetBytes("ABCD").CopyTo(image, 0x58); // a string right after it
            Assert.True(PrintableRun.Touches(image, 0x57));
            Assert.True(PrintableRun.Touches(image, 0x56));    // a neighbour of the run
            Assert.False(PrintableRun.Touches(image, 0x50));
            image = new byte[256];
            Encoding.ASCII.GetBytes("ABC").CopyTo(image, 0x10); // three is not a string
            Assert.False(PrintableRun.Touches(image, 0x11));
            image[0xFF] = 0x41; image[0xFE] = 0x41; image[0xFD] = 0x41; image[0xFC] = 0x41;
            Assert.True(PrintableRun.Touches(image, 0xFF));    // at the end of the image
            Assert.True(PrintableRun.Touches(image, 0xB0 + 0x4A, 2));
        }

        // A plausible 2022-layout image (CPUT 45 °C, fan level 20 at 0x34, a tach at 0xB0)
        private static byte[] Image2022() {
            byte[] ec = new byte[256];
            ec[0x57] = 45;
            ec[0x34] = 20;
            ec[0x35] = 22;
            ec[0x11] = 0xFF;
            ec[0xB0] = 0xD0; ec[0xB1] = 0x07;   // 2000 rpm
            return ec;
        }

        // T4: ASCII at a signal register, or next to one: no layout
        [Fact]
        public void AutoDetector_AsciiAtASignalRegister_DetectsNothing() {
            Assert.NotNull(AutoDetector.Detect("9ZZZ", Image2022()));

            // The 8C77 image: 0x34 (the 2022 fan level) is in the ID string at 0x2C-0x3A
            using var rig = new TestRig();
            BiosOnlySessionTests.Fill8C77Image(rig.Ec, 0x00);
            Assert.Null(AutoDetector.Detect("8C77", rig.Ec.Data));

            foreach((byte register, int size) in AutoDetector.SignalRegisters) {
                byte[] ec = Image2022();
                Encoding.ASCII.GetBytes("XY").CopyTo(ec, register + size);         // right after it
                Encoding.ASCII.GetBytes("WV").CopyTo(ec, Math.Max(0, register - 2)); // right before it
                for(int i = 0; i < size; i++)
                    ec[register + i] = (byte) 'Q';
                Assert.Null(AutoDetector.Detect("9ZZZ", ec));
            }
        }

        // T4: what the detector suggests is a BIOS-only model without a control register
        [Fact]
        public void AutoDetector_SuggestsNoControlRegister() {
            foreach(byte[] ec in new[] { Image2022(), Image2023() }) {
                PlatformPreset p = AutoDetector.Detect("9ZZZ", ec)!;
                Assert.NotNull(p);
                Assert.Equal(FanControlKind.Bios, p.FanControl);
                Assert.Equal(new byte[12], new[] { p.FanLevelReg0, p.FanLevelReg1, p.FanRateReadReg0, p.FanRateReadReg1,
                    p.FanRateWriteReg0, p.FanRateWriteReg1, p.FanSpeedReg0, p.FanSpeedReg1,
                    p.CountdownReg, p.ManualReg, p.ModeReg, p.SwitchReg });
                Assert.Equal(0, p.TempCpuReg);
                Assert.Equal(0, p.TempGpuReg);
            }
        }

        // A plausible 2023+-layout image (CPUT 0xFF, fan level 20 at 0x11)
        private static byte[] Image2023() {
            byte[] ec = new byte[256];
            ec[0x57] = 0xFF;
            ec[0x11] = 20;
            ec[0x34] = 0xFF;
            return ec;
        }

    }

}
