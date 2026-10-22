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

    // R1, the rule that replaces the source scan: a mode read from the EC is sent again
    // (a program's restore on its end, constant speed) only from a mode register verified
    // to hold what BIOS 0x1A last sent (8BAC: EC 0x59, OGHM, written by the ACPI method
    // behind 0x1A). From any other register never: on an 8C77-like image the default
    // ModeReg 0x95 reads 0x44 (the battery name), and 0x1A with that changes the power
    // limits. Without a verified register only the mode OmenMon last sent, or nothing.
    [Collection(HardwareStateCollection.Name)]
    public class BiosOnlyModeReplayTests : IDisposable {

        private const string Program = "ModeReplayTest";
        private readonly int savedProgramInterval = Config.UpdateProgramInterval;

        public void Dispose() {
            Config.UpdateProgramInterval = this.savedProgramInterval;
            Config.FanProgram.Remove(Program);
        }

        // An 8BAC-like BIOS-only model with its verified mode register
        private static PlatformPreset Board8Bac() {
            PlatformPreset p = BiosOnlySessionTests.Board8C77();
            p.ProductId = "8BAC";
            p.ModeReg = 0x59;
            p.ModeRegVerified = true;
            return p;
        }

        // A rig whose BIOS writes 0x1A's data[1] into EC 0x59, as GC1A does
        private static TestRig Rig(PlatformPreset preset, byte oghm) {
            var rig = new TestRig(preset: preset);
            BiosOnlySessionTests.Fill8C77Image(rig.Ec, 0x00);
            rig.Ec.Data[0x59] = oghm;
            rig.Bios.OnFanMode = m => rig.Ec.Data[0x59] = m;
            rig.SetCpuTemperature(45);
            rig.Platform.UpdateTemperature();
            return rig;
        }

        private static List<byte> ModesSent(TestRig rig) {
            return rig.Bios.Calls.Where(c => c.Type == 0x1A).Select(c => c.Data[1]).ToList();
        }

        // The core rule, as a table
        [Fact]
        public void Choose_OnlyAVerifiedRegisterIsReplayed() {
            Func<int, bool> defined = v => Enum.IsDefined(typeof(BiosData.FanMode), (byte) v);

            // Verified register with a defined mode: that mode, whatever was sent last
            Assert.Equal(0x31, ModeReplay.Choose(true, 0x31, 0x30, defined));
            Assert.Equal(0x31, ModeReplay.Choose(true, 0x31, null, defined));
            // Verified register with an undefined byte: nothing
            Assert.Null(ModeReplay.Choose(true, 0x44, 0x30, defined));
            // Verified register showing no mode (failed read, 0x00): the last mode sent
            Assert.Equal(0x30, ModeReplay.Choose(true, null, 0x30, defined));
            Assert.Null(ModeReplay.Choose(true, null, null, defined));
            // No verified register: the register is never asked, only the last mode sent
            Assert.Equal(0x30, ModeReplay.Choose(false, 0x44, 0x30, defined));
            Assert.Null(ModeReplay.Choose(false, 0x44, null, defined));
            Assert.Null(ModeReplay.Choose(false, 0x31, null, defined));

            // 0x00 (no 0x1A since boot) and a failed read are no mode
            Assert.Null(ModeReplay.RegisterMode(true, 0x00));
            Assert.Null(ModeReplay.RegisterMode(false, 0x31));
            Assert.Equal(0x03, ModeReplay.RegisterMode(true, 0x03));
        }

        // 8BAC-like: the program restores the mode the register showed before it, and
        // constant speed re-sends the mode in effect, both read from 0x59
        [Fact]
        public void VerifiedRegister_ProgramAndConstantSpeedReplayIt() {
            Config.UpdateProgramInterval = 5;
            using TestRig rig = Rig(Board8Bac(), 0x31);
            TestRig.AddProgram(Program, (0, 25, 28));   // FanMode.Default (0x30)
            IFanArray fans = rig.Platform.Fans;
            Assert.True(fans.ModeVerified);
            Assert.Equal(BiosData.FanMode.Performance, fans.GetMode());
            Assert.Null(fans.LastModeSent);

            var program = new FanProgram(rig.Platform, (a, b) => { });
            program.Run(Program);
            Assert.Equal(0x30, rig.Ec.Data[0x59]);
            program.Terminate();
            Assert.Equal(new byte[] { 0x30, 0x31 }, ModesSent(rig).Distinct().ToArray());
            Assert.Equal(0x31, ModesSent(rig).Last());

            // Constant speed: the levels, then the mode the register shows (0x31)
            rig.Bios.Calls.Clear();
            FanActions.Apply(FanActions.Request.Constant, fans, program, false, false, level0: 30, level1: 33);
            Assert.Equal(new byte[] { 0x31 }, ModesSent(rig).ToArray());
            Assert.Empty(rig.Ec.Writes);
        }

        // 8C77-like image (0x95 = 0x44) without a verified register: 0x44 is never sent,
        // nothing is read from 0x95, and only the mode OmenMon sent is ever re-sent
        [Fact]
        public void UnverifiedImage_NeverReplaysTheRegister() {
            Config.UpdateProgramInterval = 5;
            using TestRig rig = Rig(BiosOnlySessionTests.Board8C77(), 0x00);
            TestRig.AddProgram(Program, (0, 25, 28));
            IFanArray fans = rig.Platform.Fans;
            Assert.False(fans.ModeVerified);
            Assert.Null(fans.GetMode());
            Assert.Null(fans.GetModeToReplay());

            var program = new FanProgram(rig.Platform, (a, b) => { });
            program.Run(Program);
            program.Terminate();                       // nothing to restore
            FanActions.Apply(FanActions.Request.Constant, fans, program, false, false, level0: 30, level1: 33);
            Assert.Equal(new byte[] { 0x30 }, ModesSent(rig).ToArray());

            fans.SetMode(BiosData.FanMode.LegacyQuiet);
            Assert.Equal(BiosData.FanMode.LegacyQuiet, fans.GetModeToReplay());
            Assert.DoesNotContain((byte) 0x44, ModesSent(rig));
            Assert.DoesNotContain((byte) 0x95, rig.Ec.Reads);
            Assert.DoesNotContain((byte) 0x59, rig.Ec.Reads);
        }

        // An EC model whose mode register is not verified (the default 0x95 holding 0x44):
        // the register is shown, never replayed
        [Fact]
        public void UnverifiedEcModel_ReplaysOnlyTheModeSent() {
            using var rig = new TestRig(preset: PlatformPreset.Default);
            BiosOnlySessionTests.Fill8C77Image(rig.Ec, 0x00);
            IFanArray fans = rig.Platform.Fans;
            Assert.False(fans.ModeVerified);
            Assert.Equal((BiosData.FanMode) 0x44, fans.GetMode());   // display only
            Assert.Null(fans.GetModeToReplay());
            FanActions.ReapplyMode(fans);
            Assert.Equal(0, rig.Bios.Count(0x1A));
            fans.SetMode(BiosData.FanMode.Cool);
            FanActions.ReapplyMode(fans);
            Assert.Equal(new byte[] { 0x50, 0x50 }, ModesSent(rig).ToArray());
        }

        // A verified register showing no mode: 0x00 (no 0x1A since boot) is shown as
        // unknown and never sent; a failed read falls back to the mode OmenMon sent; an
        // undefined byte is not sent
        [Fact]
        public void VerifiedRegister_NoModeFailedReadOrUndefined() {
            using TestRig rig = Rig(Board8Bac(), 0x00);
            IFanArray fans = rig.Platform.Fans;
            Assert.Null(fans.GetMode());
            Assert.Null(fans.GetModeToReplay());
            FanActions.ReapplyMode(fans);
            Assert.Equal(0, rig.Bios.Count(0x1A));

            fans.SetMode(BiosData.FanMode.Default);        // 0x59 = 0x30 now
            rig.Ec.FailingReads.Add(0x59);
            Assert.Null(fans.GetMode());
            Assert.Equal(BiosData.FanMode.Default, fans.GetModeToReplay());
            rig.Ec.FailingReads.Clear();

            rig.Ec.Data[0x59] = 0x44;                       // not a mode: nothing
            Assert.Null(fans.GetModeToReplay());
            rig.Bios.Calls.Clear();
            FanActions.ReapplyMode(fans);
            Assert.Empty(ModesSent(rig));
            Assert.Empty(rig.Ec.Writes);
        }

    }

}
