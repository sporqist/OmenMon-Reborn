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

    // The GUI's 0x10 heartbeat on a BIOS-only board (8BAC, 8C77): it runs only while the
    // GUI itself has a level armed (a program, constant speed, a preset, Max Fan, a
    // guard raise), never for a level someone else armed (the -FanCheck hold, CLI
    // -Bios FanLevel, another tool). On these boards a level holds ~124 s after the last
    // 0x10/0x2E, and only those two refresh it; before, the heartbeat kept a foreign
    // level alive for as long as the GUI ran while it showed Auto (8C77, -FanCheck hold
    // then the GUI: 30/33 for 10 min). EC boards keep the heartbeat as it was.
    [Collection(HardwareStateCollection.Name)]
    public class BiosOnlyHeartbeatTests : IDisposable {

        private const string ProgramName = "HeartbeatTestProgram";

        private readonly int savedProgramInterval = Config.UpdateProgramInterval;

        public void Dispose() {
            Config.UpdateProgramInterval = this.savedProgramInterval;
            Config.FanProgram.Remove(ProgramName);
        }

        // A BIOS-only board, cool (45 °C), the GPU parked
        private static TestRig Rig(Fake8BacBios? bios = null) {
            var rig = new TestRig(preset: BiosOnlySessionTests.Board8C77(), sharedBios: bios);
            BiosOnlySessionTests.Fill8C77Image(rig.Ec, 0x00);
            rig.SetCpuTemperature(45);
            rig.Ec.GpuTemperature = 0;
            return rig;
        }

        // The BIOS calls that write or keep a level alive: 0x10, 0x2E, 0x27, 0x1A
        private static List<uint> Writes(TestRig rig, int from = 0) {
            return rig.Bios.Calls.Skip(from).Select(c => c.Type)
                .Where(t => t == 0x10 || t == 0x2E || t == 0x27 || t == 0x1A).ToList();
        }

        // The GUI starts (on Auto: no program, nothing set) while another tool's level is
        // held: no 0x10 at all, and the BIOS curve takes over when that level expires
        [Fact]
        public void GuiStartsWhileAForeignLevelIsHeld_NoHeartbeat_TheCurveTakesOver() {
            using TestRig rig = Rig();
            Assert.Equal(FanControlKind.Bios, rig.Fans.Control);

            // Another tool (CLI -Bios FanLevel, ...): 0x10 + 0x2E 30/33
            rig.BiosCtl.SetFanLevelWithoutGuard(new byte[] { 30, 33 });
            Assert.Equal(new byte[] { 30, 33 }, rig.Bios.ActiveLevels);
            rig.Bios.Calls.Clear();

            var gui = new GuiSession(rig);
            Assert.False(rig.Fans.HeartbeatAllowed);
            gui.Run(40);    // 120 s
            Assert.Equal(new byte[] { 30, 33 }, rig.Bios.ActiveLevels);
            gui.Run(2);     // 126 s: expired
            Assert.Null(rig.Bios.ActiveLevels);
            Assert.Equal(rig.Bios.CurveLevels, rig.Bios.MeasuredLevels);

            gui.Run(200);   // another 10 min
            Assert.Empty(Writes(rig));
            Assert.Equal(rig.Bios.CurveLevels, rig.Bios.MeasuredLevels);
            Assert.False(rig.Fans.HeartbeatAllowed);
        }

        // Constant speed: the GUI's own level, kept alive by the heartbeat alone (no
        // countdown register, no further 0x2E)
        [Fact]
        public void ConstantSpeed_TheHeartbeatKeepsTheGuisLevel() {
            using TestRig rig = Rig();
            var gui = new GuiSession(rig);
            gui.Set(FanActions.Request.Constant, level0: 30, level1: 33);
            Assert.True(rig.Fans.HeartbeatAllowed);
            int from = rig.Bios.Calls.Count;

            gui.Run(200);   // 10 min
            Assert.Equal(new byte[] { 30, 33 }, rig.Bios.ActiveLevels);
            List<uint> writes = Writes(rig, from);
            Assert.DoesNotContain(0x2EU, writes);
            Assert.True(writes.Count(t => t == 0x10) >= 600 / 30 - 1);
        }

        // A program whose ticks are further apart than the ~124 s hold: the heartbeat keeps
        // its level alive between them
        [Fact]
        public void Program_TheHeartbeatKeepsItsLevelBetweenTicks() {
            using TestRig rig = Rig();
            TestRig.AddProgram(ProgramName, (0, 30, 33), (60, 40, 43));
            Config.UpdateProgramInterval = 60;   // a tick every 180 s
            var gui = new GuiSession(rig);
            gui.AutoConfig(ProgramName);
            Assert.True(rig.Fans.HeartbeatAllowed);
            int from = rig.Bios.Calls.Count;

            for(int pass = 0; pass < 200; pass++) {
                gui.Pass((pass + 1) % Config.UpdateProgramInterval == 0);
                rig.Advance(3);
                Assert.Equal(new byte[] { 30, 33 }, rig.Bios.ActiveLevels);
            }
            List<uint> writes = Writes(rig, from);
            Assert.Equal(200 / 60, writes.Count(t => t == 0x2E));
            Assert.True(writes.Count(t => t == 0x10) > writes.Count(t => t == 0x2E));   // 0x10 on its own
        }

        // Max Fan: 0x27 [1] needs a 0x10 before it (8C77, measured), so SetMax arms it
        // itself; while it is on the heartbeat runs, once it is off the GUI is quiet again
        [Fact]
        public void MaxFan_ArmedAndKeptAlive_QuietOnceOff() {
            using TestRig rig = Rig();
            var gui = new GuiSession(rig);
            rig.Bios.Calls.Clear();

            gui.TrayMax(true);
            List<uint> types = rig.Bios.Calls.Select(c => c.Type).ToList();
            int max = types.IndexOf(0x27);
            Assert.True(max > 0);
            Assert.Equal(0x10U, types[max - 1]);
            Assert.True(rig.Fans.HeartbeatAllowed);

            int from = rig.Bios.Calls.Count;
            gui.Run(40);
            Assert.True(Writes(rig, from).Count(t => t == 0x10) >= 120 / 30);

            gui.TrayMax(false);
            types = rig.Bios.Calls.Select(c => c.Type).ToList();
            Assert.Equal(0x10U, types[types.LastIndexOf(0x27) - 1]);
            Assert.False(rig.Fans.HeartbeatAllowed);
            from = rig.Bios.Calls.Count;
            gui.Run(200);
            Assert.Empty(Writes(rig, from));
        }

        // After the GUI's own hand-back (a program terminated): the hand-back level, then
        // silence; the curve takes over and the heartbeat does not come back after the
        // quiet period (before, it resumed after 150 s)
        [Fact]
        public void AfterTheGuisOwnHandBack_Quiet() {
            using TestRig rig = Rig();
            TestRig.AddProgram(ProgramName, (0, 30, 33), (60, 40, 43));
            Config.UpdateProgramInterval = 5;
            var gui = new GuiSession(rig);
            gui.AutoConfig(ProgramName);
            gui.Run(20);

            gui.Program.Terminate();
            Assert.Equal(new byte[] { 30, 33 }, rig.Bios.LevelWrites.Last());   // the hand-back level
            Assert.False(rig.Fans.Guard!.HoldsLevel);
            int from = rig.Bios.Calls.Count;

            gui.Run(200);   // 10 min
            Assert.DoesNotContain(Writes(rig, from), t => t == 0x10 || t == 0x2E);
            Assert.Null(rig.Bios.ActiveLevels);
            Assert.Equal(rig.Bios.CurveLevels, rig.Bios.MeasuredLevels);
            Assert.False(rig.Fans.HeartbeatAllowed);

            // The next level the GUI writes brings the heartbeat back
            gui.Set(FanActions.Request.Constant, level0: 30, level1: 33);
            Assert.True(rig.Fans.HeartbeatAllowed);
        }

        // A guard raise is a level the GUI wrote: the heartbeat keeps it alive. Constant
        // speed below the floor while cool; warming up raises it to the floor
        [Fact]
        public void GuardRaise_IsTheGuisLevel_HeartbeatRuns() {
            using TestRig rig = Rig();
            var gui = new GuiSession(rig);
            gui.Run(1);     // a fresh reading: cool
            gui.Set(FanActions.Request.Constant, level0: 10, level1: 12);
            Assert.Equal(new byte[] { 10, 12 }, rig.Bios.ActiveLevels);
            Assert.True(rig.Fans.HeartbeatAllowed);
            rig.SetCpuTemperature(62);
            gui.Run(2);
            Assert.Equal(new byte[] { 20, 22 }, rig.Bios.ActiveLevels);   // raised to the floor
            int from = rig.Bios.Calls.Count;
            gui.Run(100);
            Assert.Equal(new byte[] { 20, 22 }, rig.Bios.ActiveLevels);
            Assert.True(Writes(rig, from).Count(t => t == 0x10) >= 300 / 30 - 1);
        }

        // The -FanCheck hold ended by a GUI start (a second process on the same board):
        // the check writes nothing more, the GUI (on Auto) writes nothing either, and the
        // hold's floor expires into the BIOS curve
        [Fact]
        public void FanCheckHoldEndedByAGuiStart_NoWriteFromEither() {
            using TestRig cli = Rig();
            cli.Bios.FanTable = new byte[] { 2, 3, 20, 23, 40, 40, 43, 60, 60, 63, 80 };
            int holdSecond = -1;
            int callsAtGui = -1;
            FanCheckResult r = FanCheckRunner.Run(cli.Platform, s => {
                cli.Advance(s);
                if(holdSecond >= 0)
                    holdSecond++;
            }, line => {
                if(line.StartsWith("Fan check done"))
                    holdSecond = 0;
            }, TestContext.Current.CancellationToken, new FanCheckHold {
                GuiStarted = () => {
                    if(holdSecond < 95)
                        return false;
                    callsAtGui = cli.Bios.Calls.Count;
                    return true;
                } });
            Assert.Equal(FanCheckHoldEnd.GuiStarted, r.HoldEnd);
            Assert.False(r.HandedBack);
            Assert.Equal(new byte[] { 20, 22 }, cli.Bios.ActiveLevels);

            using TestRig guiRig = Rig(cli.Bios);
            var gui = new GuiSession(guiRig);
            gui.Run(100);   // 300 s
            Assert.Empty(Writes(cli, callsAtGui));
            Assert.Null(cli.Bios.ActiveLevels);
            Assert.Equal(cli.Bios.CurveLevels, cli.Bios.MeasuredLevels);
            Assert.False(guiRig.Fans.HeartbeatAllowed);
        }

        // EC boards: unchanged. The heartbeat runs without a level of OmenMon's own (it
        // keeps Performance Control alive) and so still keeps a foreign level; it is quiet
        // for 150 s after a hand-back, then resumes; Max Fan sends no extra 0x10
        [Fact]
        public void EcBoard_HeartbeatUnchanged() {
            using TestRig rig = new TestRig();
            rig.SetCpuTemperature(45);
            Assert.Equal(FanControlKind.Ec, rig.Fans.Control);

            rig.BiosCtl.SetFanLevelWithoutGuard(new byte[] { 30, 33 });
            rig.Bios.Calls.Clear();
            var gui = new GuiSession(rig);
            Assert.True(rig.Fans.HeartbeatAllowed);
            gui.Run(100);   // 300 s
            Assert.True(Writes(rig).Count(t => t == 0x10) >= 300 / 30);
            Assert.Equal(new byte[] { 30, 33 }, rig.Bios.ActiveLevels);   // kept alive, as before

            // Max Fan: 0x27 without an arm of its own
            int from = rig.Bios.Calls.Count;
            gui.TrayMax(true);
            List<uint> maxCalls = rig.Bios.Calls.Skip(from).Select(c => c.Type).ToList();
            Assert.Equal(new uint[] { 0x27 }, maxCalls.Where(t => t == 0x10 || t == 0x27));
            gui.TrayMax(false);
            Assert.True(rig.Fans.HeartbeatAllowed);

            // A hand-back: quiet for the quiet period, then the heartbeat resumes
            rig.Fans.SetLevels(new byte[] { 30, 33 });
            rig.Fans.Release();
            Assert.False(rig.Fans.HeartbeatAllowed);
            from = rig.Bios.Calls.Count;
            gui.Run(49);    // 147 s
            Assert.DoesNotContain(0x10U, Writes(rig, from));
            gui.Run(15);
            Assert.True(rig.Fans.HeartbeatAllowed);
            Assert.Contains(0x10U, Writes(rig, from));
        }

    }

}
