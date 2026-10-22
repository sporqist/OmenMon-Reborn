  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System.Text;
using OmenMon.Core;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // A BIOS-only board through a whole GUI session (AutoConfig, a fan program, constant
    // speed, Fan Off and Max from the tray, every preset, thermal panic, the main form's
    // modes, exit), over an EC image laid out like 8C77's where OmenMon's default fan
    // registers point (measured): an ID string at 0x2C-0x3A, the EC's SMBus host
    // interface at 0x62/0x63 (0x16 = battery address 0x0B << 1, 0x20 = SBS command
    // ManufacturerName) with the battery manufacturer name after it, part of the battery
    // serial at 0x90-0x9F (0x95 = 0x44), the keyboard colour at 0x57, flag bits at 0x58.
    // 0xF4 has no name in the AML: both 0x00 (measured) and 0x02 (would make the legacy
    // path write it every 15 s) are run.
    [Collection(HardwareStateCollection.Name)]
    public class BiosOnlySessionTests : IDisposable {

        private readonly int savedProgramInterval = Config.UpdateProgramInterval;
        private readonly bool savedFanLevelUseEc = Config.FanLevelUseEc;
        private readonly bool savedFanLevelNeedManual = Config.FanLevelNeedManual;
        private readonly bool savedExtendAlways = Config.FanCountdownExtendAlways;

        public void Dispose() {
            Config.UpdateProgramInterval = this.savedProgramInterval;
            Config.FanLevelUseEc = this.savedFanLevelUseEc;
            Config.FanLevelNeedManual = this.savedFanLevelNeedManual;
            Config.FanCountdownExtendAlways = this.savedExtendAlways;
            Config.FanProgram.Remove(ProgramName);
        }

        internal const string ProgramName = "BiosOnlyTestPower";

        // The 8C77 entry as F4 has it (FanControl Bios, CPU 0xB2, GPU 0xB4)
        internal static PlatformPreset Board8C77() {
            return new PlatformPreset {
                ProductId = "8C77",
                DisplayName = "test 8C77",
                FanControl = FanControlKind.Bios,
                TempCpuReg = FakeEcRegisters.CpuTempRegister,
                TempGpuReg = FakeEcRegisters.GpuTempRegister };
        }

        // Fills the EC like 8C77's (see the class comment); temperatures stay the rig's
        internal static void Fill8C77Image(FakeEcRegisters ec, byte switchByte) {
            void Ascii(int at, string text) {
                byte[] b = Encoding.ASCII.GetBytes(text);
                Array.Copy(b, 0, ec.Data, at, b.Length);
            }
            Ascii(0x2C, "TESTBOARDID0001");          // 0x2C-0x3A, 15 characters
            ec.Data[0x62] = 0x16;                     // SMBus address (battery 0x0B << 1)
            ec.Data[0x63] = 0x20;                     // SBS command: ManufacturerName
            Ascii(0x64, "BATTMAKERNAME");             // the EC's SMBus data buffer
            Ascii(0x90, "0123456789ABCDEF");          // part of the battery serial
            ec.Data[0x95] = 0x44;                     // what the default ModeReg reads
            ec.Data[0x57] = 0x40;                     // keyboard WASD blue (CPUT)
            ec.Data[0x58] = 0x43;                     // flag bits (RTMP)
            ec.Data[0x59] = 0x00;                     // OGHM (TMP1)
            ec.Data[0x11] = 0x14;                     // CPU fan readback
            ec.Data[0x12] = 0x25;
            ec.Data[0xF4] = switchByte;
        }

        // The modes OmenMon may send: every preset's and every configured program's
        internal static HashSet<byte> AllowedModes() {
            var modes = new HashSet<byte>();
            foreach(PerformancePreset p in PerformancePresets.All)
                modes.Add((byte) PerformancePresets.Resolve(p).FanMode);
            foreach(FanProgramData program in Config.FanProgram.Values)
                modes.Add((byte) program.FanMode);
            return modes;
        }

        // Runs the whole session; returns the ranges of BIOS calls (indices into
        // Bios.Calls) that a Fan Off request made while the system was cool, the only
        // place 0x2E [0, 0] may appear
        internal static List<(int From, int To)> RunSession(GuiSession s) {

            TestRig rig = s.Rig;
            var coolOff = new List<(int, int)>();
            void CoolOff(Action action) {
                int from = rig.Bios.Calls.Count;
                action();
                coolOff.Add((from, rig.Bios.Calls.Count));
            }

            Config.UpdateProgramInterval = 5;
            Config.FanProgram[ProgramName] = new FanProgramData(ProgramName,
                BiosData.FanMode.Performance, BiosData.GpuPowerLevel.Max,
                new SortedDictionary<byte, byte[]> {
                    [0] = new byte[] { 20, 22 }, [60] = new byte[] { 35, 35 }, [80] = new byte[] { 50, 50 } });

            rig.SetCpuTemperature(45);
            rig.Ec.GpuTemperature = 48;

            // Start with AutoConfig: the program for 20 ticks, warming up on the way
            s.AutoConfig(ProgramName);
            for(int tick = 0; tick < 20; tick++) {
                if(tick == 10) rig.SetCpuTemperature(66);
                s.Run(Config.UpdateProgramInterval);
            }

            // Constant speed for 20 monitor passes
            rig.SetCpuTemperature(52);
            s.Set(FanActions.Request.Constant, level0: 30, level1: 33);
            s.Run(20);

            // Tray Fan Off on and off: cool (0/0 allowed), then warm (the floor)
            rig.SetCpuTemperature(40);
            rig.Ec.GpuTemperature = 0;   // parked
            s.Run(2);
            CoolOff(() => s.TrayFanOff(true));
            s.Run(5);
            s.TrayFanOff(false);
            s.Run(3);
            rig.SetCpuTemperature(62);
            rig.Ec.GpuTemperature = 55;
            s.Run(2);
            s.TrayFanOff(true);
            s.Run(5);
            s.TrayFanOff(false);
            s.Run(3);

            // Tray Max on and off
            s.TrayMax(true);
            s.Run(3);
            s.TrayMax(false);
            s.Run(3);

            // Every preset
            foreach(PerformancePreset preset in PerformancePresets.All) {
                s.Set(FanActions.Request.Auto, preset: preset);
                s.Run(4);
            }

            // Thermal panic: enter, hold, leave
            rig.SetCpuTemperature(95);
            s.Run(6);
            rig.SetCpuTemperature(70);
            s.Run(6);

            // The main form's other modes
            s.Set(FanActions.Request.Max);
            s.Run(3);
            s.Set(FanActions.Request.Off);
            s.Run(3);
            s.Set(FanActions.Request.Program, program: ProgramName);
            s.Run(Config.UpdateProgramInterval * 3);
            s.Set(FanActions.Request.Constant, bothMax: true);
            s.Run(3);
            rig.SetCpuTemperature(40);
            rig.Ec.GpuTemperature = 0;
            s.Run(2);
            CoolOff(() => s.Set(FanActions.Request.Constant, bothMin: true));
            s.Run(5);
            s.Set(FanActions.Request.Constant, level0: 25, level1: 28);
            s.Run(10);

            // Terminate and exit
            s.Program.Run(ProgramName);
            s.Run(Config.UpdateProgramInterval);
            s.Program.Terminate();
            s.Exit();

            return coolOff;
        }

        // The checks T1 and T2 share: no EC write at all and the image unchanged, EC
        // reads only of the model's temperature registers, BIOS 0x1A only with modes
        // OmenMon chose (never the 0x44 the default mode register holds), and 0x2E [0, 0]
        // only inside a cool Fan Off request
        internal static void AssertBiosOnly(TestRig rig, byte[] image, List<(int From, int To)> coolOff, params byte[] tempRegisters) {
            AssertBiosOnly(rig, image, coolOff, Array.Empty<byte>(), tempRegisters);
        }

        // The same, with modes a verified mode register may add (its value at the start:
        // replayed from it) and the registers that may be read (temperatures, the
        // verified mode register)
        internal static void AssertBiosOnly(TestRig rig, byte[] image, List<(int From, int To)> coolOff, IEnumerable<byte> registerModes, params byte[] readable) {

            Assert.Empty(rig.Ec.Writes);
            Assert.Equal(image, rig.Ec.Data);
            Assert.Empty(rig.Ec.Reads.Except(readable));

            HashSet<byte> allowed = AllowedModes();
            allowed.UnionWith(registerModes);
            var modes = rig.Bios.Calls.Where(c => c.Type == 0x1A).Select(c => c.Data[1]).ToList();
            Assert.NotEmpty(modes);
            Assert.All(modes, m => Assert.Contains(m, allowed));
            Assert.DoesNotContain((byte) 0x44, modes);

            bool sawOff = false;
            for(int i = 0; i < rig.Bios.Calls.Count; i++) {
                var call = rig.Bios.Calls[i];
                if(call.Type != 0x2E || call.Data[0] != 0 || call.Data[1] != 0)
                    continue;
                Assert.True(coolOff.Any(w => i >= w.From && i < w.To), "0x2E [0, 0] outside a cool Fan Off at call " + i);
                sawOff = true;
            }
            Assert.True(sawOff, "the cool Fan Off path never ran");
            Assert.True(rig.Bios.Count(0x2E) > 20, rig.Bios.Count(0x2E) + " level writes");

        }

        // T1: a BIOS-only 8C77 through the session: nothing reaches the EC
        [Theory]
        [InlineData((byte) 0x00)]
        [InlineData((byte) 0x02)]
        public void BiosOnly8C77_GuiSession_WritesNothingToTheEc(byte switchByte) {
            Config.FanLevelUseEc = false;
            Config.FanLevelNeedManual = false;
            Config.FanCountdownExtendAlways = false;
            using var rig = new TestRig(preset: Board8C77());
            Fill8C77Image(rig.Ec, switchByte);
            rig.SetCpuTemperature(45);
            rig.Ec.GpuTemperature = 48;
            byte[] image = (byte[]) rig.Ec.Data.Clone();
            rig.Ec.Reads.Clear();

            var session = new GuiSession(rig);
            List<(int, int)> coolOff = RunSession(session);

            // The temperatures moved (the rig's sensors); the image must be as it was
            // apart from them, which the test put there itself
            image[FakeEcRegisters.CpuTempRegister] = rig.Ec.Data[FakeEcRegisters.CpuTempRegister];
            image[FakeEcRegisters.GpuTempRegister] = rig.Ec.Data[FakeEcRegisters.GpuTempRegister];
            AssertBiosOnly(rig, image, coolOff, FakeEcRegisters.CpuTempRegister, FakeEcRegisters.GpuTempRegister);
        }

        // T1 also with the settings that make the legacy path write the EC (levels and
        // manual mode through the EC, the countdown always extended): a BIOS-only model
        // ignores them
        [Fact]
        public void BiosOnly8C77_EcSettingsOn_StillWritesNothingToTheEc() {
            Config.FanLevelUseEc = true;
            Config.FanLevelNeedManual = true;
            Config.FanCountdownExtendAlways = true;
            using var rig = new TestRig(preset: Board8C77());
            Fill8C77Image(rig.Ec, 0x02);
            byte[] image = (byte[]) rig.Ec.Data.Clone();

            List<(int, int)> coolOff = RunSession(new GuiSession(rig));

            image[FakeEcRegisters.CpuTempRegister] = rig.Ec.Data[FakeEcRegisters.CpuTempRegister];
            image[FakeEcRegisters.GpuTempRegister] = rig.Ec.Data[FakeEcRegisters.GpuTempRegister];
            AssertBiosOnly(rig, image, coolOff, FakeEcRegisters.CpuTempRegister, FakeEcRegisters.GpuTempRegister);
        }

        // Control: the same session on the legacy default layout (what 8C77 got before)
        // writes the EC (0xF4 from the tray's Fan Off), so the checks above can fail
        [Fact]
        public void LegacyDefaultLayout_SameSession_WritesTheEc() {
            Config.FanLevelUseEc = false;
            Config.FanLevelNeedManual = false;
            Config.FanCountdownExtendAlways = false;
            using var rig = new TestRig(preset: PlatformPreset.Default);
            Fill8C77Image(rig.Ec, 0x02);

            RunSession(new GuiSession(rig));

            Assert.Contains(rig.Ec.Writes, w => w.Register == 0xF4);
        }

        // On a BIOS-only model the speed is BIOS 0x2D x 100, the rate, the mode and
        // the manual/switch registers are unknown or come from the guard
        [Fact]
        public void BiosOnly_ReadsComeFromTheBiosAndTheGuard() {
            using var rig = new TestRig(preset: Board8C77());
            Fill8C77Image(rig.Ec, 0x02);
            rig.SetCpuTemperature(45);
            rig.Platform.UpdateTemperature();
            IFanArray fans = rig.Platform.Fans;

            Assert.Equal(FanControlKind.Bios, fans.Control);
            Assert.Null(fans.GetMode());
            Assert.False(fans.GetOff());       // 0xF4 = 0x02 would say "off" on the legacy path
            Assert.False(fans.GetManual());
            Assert.Equal(0, fans.GetCountdown());
            Assert.Equal(AbsentComponent.Unknown, fans.Fan[0].GetRate());
            Assert.Equal(rig.Bios.MeasuredLevels[0] * 100, fans.Fan[0].GetSpeed());
            Assert.Equal(rig.Bios.MeasuredLevels[1] * 100, fans.Fan[1].GetSpeed());

            fans.SetLevels(new byte[] { 30, 33 });
            Assert.True(fans.GetManual());
            Assert.InRange(fans.GetCountdown(), 120, 124);
            rig.Advance(100);
            Assert.InRange(fans.GetCountdown(), 20, 24);
            Assert.Equal(3000, fans.Fan[0].GetSpeed());

            // Fan Off while cool: 0/0, reported off; back on: handed back, not off
            rig.SetCpuTemperature(40);
            rig.Ec.GpuTemperature = 0;
            rig.Platform.UpdateTemperature();
            fans.SetOff(true);
            Assert.True(fans.GetOff());
            Assert.True(rig.Bios.FansOff);
            fans.SetOff(false);
            Assert.False(fans.GetOff());
            Assert.False(fans.GetManual());
            Assert.False(rig.Bios.FansOff);
            Assert.Empty(rig.Ec.Writes);
        }

        // A mode that is not defined is never sent (none comes from config or a preset;
        // this is the backstop), and the last mode sent is remembered for display
        [Fact]
        public void SetMode_SendsOnlyDefinedModes() {
            using var rig = new TestRig(preset: Board8C77());
            rig.Platform.Fans.SetMode((BiosData.FanMode) 0x44);
            Assert.Equal(0, rig.Bios.Count(0x1A));
            Assert.Null(rig.Platform.Fans.LastModeSent);
            rig.Platform.Fans.SetMode(BiosData.FanMode.Performance);
            Assert.Equal(1, rig.Bios.Count(0x1A));
            Assert.Equal(BiosData.FanMode.Performance, rig.Platform.Fans.LastModeSent);
        }

        // A program ends by restoring the mode OmenMon sent before it, or nothing
        [Fact]
        public void ProgramTerminate_RestoresOnlyAModeOmenMonSent() {
            Config.UpdateProgramInterval = 5;
            using var rig = new TestRig(preset: Board8C77());
            Fill8C77Image(rig.Ec, 0x00);
            rig.SetCpuTemperature(45);
            TestRig.AddProgram(ProgramName, (0, 20, 22));   // FanMode.Default (0x30)
            var program = new FanProgram(rig.Platform, (a, b) => { });

            program.Run(ProgramName);
            program.Terminate();
            Assert.All(rig.Bios.Calls.Where(c => c.Type == 0x1A), c => Assert.Equal(0x30, c.Data[1]));

            rig.Platform.Fans.SetMode(BiosData.FanMode.LegacyQuiet);
            rig.Bios.Calls.Clear();
            program.Run(ProgramName);
            program.Terminate();
            Assert.Equal(new byte[] { 0x30, 0x03 }, rig.Bios.Calls.Where(c => c.Type == 0x1A).Select(c => c.Data[1]).Distinct().ToArray());
            Assert.Equal(0x03, rig.Bios.Calls.Last(c => c.Type == 0x1A).Data[1]);
        }

    }

}
