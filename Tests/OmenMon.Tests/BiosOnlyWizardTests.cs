  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System.Text;
using OmenMon.AppCli;
using OmenMon.Core;
using OmenMon.Hardware.Ec;
using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // The calibration wizard (W1-W3): it refuses a board without a verified EC map before
    // touching anything; where it runs it never asks for 0/0, arms 0x10 before 0x27,
    // restores the rate-write registers' own bytes, clamps the countdown to the register,
    // and its report carries no full EC dump
    [Collection(HardwareStateCollection.Name)]
    public class BiosOnlyWizardTests : IDisposable {

        private readonly Dictionary<string, PlatformPreset> savedModels = Config.Models;
        private readonly IEmbeddedController savedEc = Hw.Ec;

        public BiosOnlyWizardTests() {
            Config.Models = new Dictionary<string, PlatformPreset>(StringComparer.OrdinalIgnoreCase) {
                ["8C77"] = BiosOnlySessionTests.Board8C77(),
                ["EC01"] = new PlatformPreset { ProductId = "EC01", FanControl = FanControlKind.Ec } };
        }

        public void Dispose() {
            Config.Models = this.savedModels;
            Hw.Ec = this.savedEc;
        }

        // T3: a BIOS-only model refuses, with no BIOS or EC call at all
        [Fact]
        public void Wizard_OnBiosOnlyModel_RefusesWithoutAnyCall() {
            using var rig = new TestRig(preset: BiosOnlySessionTests.Board8C77());
            BiosOnlySessionTests.Fill8C77Image(rig.Ec, 0x02);
            byte[] image = (byte[]) rig.Ec.Data.Clone();
            rig.Ec.Reads.Clear();
            var program = new FanProgram(rig.Platform, (a, b) => { });
            IEmbeddedController? ecBefore = Hw.Ec;

            CliOp.CalibrationOutcome outcome = CliOp.AutoCalibrate(cancel: TestContext.Current.CancellationToken,
                fans: rig.Fans, program: program, product: "8C77");

            Assert.False(outcome.Success);
            Assert.Contains("comes from the BIOS (0x2D x 100); no calibration needed", outcome.FailureReason);
            Assert.Empty(rig.Bios.Calls);
            Assert.Empty(rig.Ec.Writes);
            Assert.Empty(rig.Ec.Reads);
            Assert.Equal(image, rig.Ec.Data);
            Assert.Same(ecBefore, Hw.Ec);            // no driver opened either
            Assert.Null(outcome.Markdown);
        }

        // W1 without a fan array (the CLI path) and on a board without an entry
        [Fact]
        public void CalibrationRefusal_ModelKinds() {
            using var rig = new TestRig();   // an EC fan array
            Assert.Contains("comes from the BIOS", CliOp.CalibrationRefusal(null, "8C77"));
            Assert.Contains("no verified EC map", CliOp.CalibrationRefusal(null, "9ZZZ"));
            Assert.Contains("no verified EC map", CliOp.CalibrationRefusal(rig.Fans, "9ZZZ"));
            Assert.Contains("no verified EC map", CliOp.CalibrationRefusal(rig.Fans, null));
            Assert.Null(CliOp.CalibrationRefusal(null, "EC01"));
            Assert.Null(CliOp.CalibrationRefusal(rig.Fans, "EC01"));

            using var bios = new TestRig(preset: BiosOnlySessionTests.Board8C77());
            Assert.Contains("comes from the BIOS", CliOp.CalibrationRefusal(bios.Fans, "EC01"));

            Assert.False(BiosFanControl.CalibrationAllowed(null, FanControlKind.Ec));
            Assert.False(BiosFanControl.CalibrationAllowed(FanControlKind.Bios, FanControlKind.Bios));
            Assert.False(BiosFanControl.CalibrationAllowed(FanControlKind.Ec, FanControlKind.Bios));
            Assert.True(BiosFanControl.CalibrationAllowed(FanControlKind.Ec, FanControlKind.Ec));
        }

        // W2: the lowest step asks for the floor, never 0/0, even while cool
        [Fact]
        public void ApplyLevel_ZeroPercent_IsTheFloorEvenWhenCool() {
            using var rig = new TestRig();
            rig.SetCpuTemperature(40);
            rig.Ec.GpuTemperature = 0;
            rig.Platform.UpdateTemperature();
            Assert.True(rig.Fans.Guard!.IsCool());

            CliOp.ApplyLevel(rig.Fans, 0);
            Assert.Equal(new byte[] { 20, 22 }, rig.Bios.LevelWrites.Last());
            CliOp.ApplyLevel(rig.Fans, 30);
            Assert.Equal(new byte[] { 20, 22 }, rig.Bios.LevelWrites.Last());   // 30 % = 16 < floor
            CliOp.ApplyLevel(rig.Fans, 70);
            Assert.Equal(new byte[] { 38, 38 }, rig.Bios.LevelWrites.Last());
            Assert.DoesNotContain(rig.Bios.LevelWrites, l => l[0] == 0 && l[1] == 0);

            // The rate registers are raised to match the floor (20 of 55 -> 37 %)
            byte xss1 = PlatformPreset.Default.FanRateWriteReg0;
            Assert.Contains(rig.Ec.Writes, w => w.Register == xss1 && w.Value == 37);
        }

        // W2: the 100 % step sends 0x10 right before 0x27 [1]
        [Fact]
        public void ApplyLevel_Max_ArmsFirst() {
            using var rig = new TestRig();
            rig.Bios.Calls.Clear();
            CliOp.ApplyLevel(rig.Fans, 100);
            var types = rig.Bios.Calls.Select(c => c.Type).ToList();
            int max = types.LastIndexOf(0x27);
            Assert.True(max > 0);
            Assert.Equal(0x10u, types[max - 1]);
            Assert.Equal(1, rig.Bios.Calls[max].Data[0]);
        }

        // W2: the restore reads the rate-WRITE register (not the read register), and the
        // countdown is clamped to the register size
        [Fact]
        public void RateWriteAndCountdown_AsTheRegistersHoldThem() {
            using var rig = new TestRig(preset: PlatformPreset.Default);
            PlatformPreset p = PlatformPreset.Default;
            rig.Ec.Data[p.FanRateReadReg0] = 11;
            rig.Ec.Data[p.FanRateWriteReg0] = 77;
            Assert.Equal(77, rig.Fans.Fan[0].GetRateWrite());
            Assert.Equal(11, rig.Fans.Fan[0].GetRate());

            rig.Fans.SetCountdown(600);
            Assert.Equal(255, rig.Ec.Data[p.CountdownReg]);
            rig.Fans.SetCountdown(120);
            Assert.Equal(120, rig.Ec.Data[p.CountdownReg]);

            // A BIOS-only model has no rate-write register to read
            using var bios = new TestRig(preset: BiosOnlySessionTests.Board8C77());
            Assert.Equal(-1, bios.Fans.Fan[0].GetRateWrite());
        }

        // W3: the report has the candidates' values per step, never a full dump
        [Fact]
        public void Report_HasNoFullDump() {
            var outcome = new CliOp.CalibrationOutcome();
            byte[] Dump(int level) {
                byte[] m = new byte[256];
                Encoding.ASCII.GetBytes("SECRETSERIAL0123").CopyTo(m, 0x90);
                m[0xB0] = (byte) (level * 2);
                return m;
            }
            foreach(int level in new[] { 0, 30, 70 })
                outcome.Samples.Add(new EcDiffScanner.Sample(level, Dump(level)));
            outcome.Scan = EcDiffScanner.Scan(outcome.Samples, null);
            var sb = new StringBuilder();
            CliOp.AppendCandidateSteps(sb, outcome);
            string report = sb.ToString();
            Assert.DoesNotContain("53 45 43 52", report);   // "SECR" as a dump row would show it
            Assert.DoesNotContain("SECRET", report);
            Assert.DoesNotContain("0x _0 _1", report);
            Assert.Contains("Candidate Registers per Step", report);
        }

    }

}
