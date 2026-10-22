  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System.Reflection;
using System.Text.RegularExpressions;
using OmenMon.Core;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // The model database with BIOS-only entries: a <Model> with <FanControl>Bios</FanControl>
    // needs no EC fan register, round-trips through load and save, and a malformed entry
    // is not loaded silently (nor are its values read as 0)
    [Collection(HardwareStateCollection.Name)]
    public class BiosOnlyModelTests : IDisposable {

        private readonly string Dir;
        private readonly string File_;
        private readonly Dictionary<FieldInfo, object?> Saved = new();

        public BiosOnlyModelTests() {
            Config.LocaleInit();
            foreach(FieldInfo f in typeof(Config).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                if(!f.IsLiteral && !f.IsInitOnly)
                    Saved[f] = f.GetValue(null);
            foreach(FieldInfo f in typeof(Config).GetFields(BindingFlags.NonPublic | BindingFlags.Static))
                if(f.IsInitOnly && f.Name.StartsWith("Loaded") && f.GetValue(null) is HashSet<string> set)
                    set.Clear();
            Config.FanProgram = new SortedList<string, FanProgramData>();
            Config.Models = new Dictionary<string, PlatformPreset>(StringComparer.OrdinalIgnoreCase);
            Config.ColorPreset = new SortedDictionary<string, BiosData.ColorTable>(Config.ColorPreset);
            Config.TemperatureSensor = new Dictionary<string, Config.TemperatureSensorData>(Config.TemperatureSensor);
            Config.GraphicsProfiles = new SortedList<string, GraphicsProfile>(StringComparer.OrdinalIgnoreCase);
            Dir = Path.Combine(Path.GetTempPath(), "omenmon-biosonly-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            File_ = Path.Combine(Dir, "OmenMon.xml");
            Config.FilePath = File_;
        }

        public void Dispose() {
            foreach(KeyValuePair<FieldInfo, object?> pair in Saved)
                pair.Key.SetValue(null, pair.Value);
            lock(Config.ModelLoadProblems)
                Config.ModelLoadProblems.Clear();
            try { Directory.Delete(Dir, true); } catch { }
        }

        private const string Fixture = @"<?xml version=""1.0"" encoding=""utf-8""?>
<OmenMon>
    <Config>
        <Models>
            <!-- a BIOS-only board: no EC fan register -->
            <Model ProductId=""8C77"" DisplayName=""BIOS-only board"">
                <FanControl>Bios</FanControl>
                <TempCpuReg>178</TempCpuReg>
                <TempGpuReg>180</TempGpuReg>
            </Model>
            <Model ProductId=""EC01"" DisplayName=""EC board"">
                <FanLevelReg0>52</FanLevelReg0>
                <FanLevelReg1>53</FanLevelReg1>
                <FanRateReadReg0>46</FanRateReadReg0>
                <FanRateReadReg1>47</FanRateReadReg1>
                <FanRateWriteReg0>44</FanRateWriteReg0>
                <FanRateWriteReg1>45</FanRateWriteReg1>
                <FanSpeedReg0>176</FanSpeedReg0>
                <FanSpeedReg1>178</FanSpeedReg1>
                <CountdownReg>99</CountdownReg>
                <ManualReg>98</ManualReg>
                <ModeReg>149</ModeReg>
                <SwitchReg>244</SwitchReg>
            </Model>
            <Model ProductId=""BAD1"" DisplayName=""EC board without a switch register"">
                <FanLevelReg0>52</FanLevelReg0>
                <FanLevelReg1>53</FanLevelReg1>
                <FanRateReadReg0>46</FanRateReadReg0>
                <FanRateReadReg1>47</FanRateReadReg1>
                <FanRateWriteReg0>44</FanRateWriteReg0>
                <FanRateWriteReg1>45</FanRateWriteReg1>
                <FanSpeedReg0>176</FanSpeedReg0>
                <FanSpeedReg1>178</FanSpeedReg1>
                <CountdownReg>99</CountdownReg>
                <ManualReg>98</ManualReg>
                <ModeReg>149</ModeReg>
            </Model>
            <Model ProductId=""BAD2"" DisplayName=""EC board with a value that is not a byte"">
                <FanLevelReg0>52</FanLevelReg0>
                <FanLevelReg1>0xZZ</FanLevelReg1>
                <FanRateReadReg0>46</FanRateReadReg0>
                <FanRateReadReg1>47</FanRateReadReg1>
                <FanRateWriteReg0>44</FanRateWriteReg0>
                <FanRateWriteReg1>45</FanRateWriteReg1>
                <FanSpeedReg0>176</FanSpeedReg0>
                <FanSpeedReg1>178</FanSpeedReg1>
                <CountdownReg>99</CountdownReg>
                <ManualReg>98</ManualReg>
                <ModeReg>149</ModeReg>
                <SwitchReg>244</SwitchReg>
            </Model>
            <Model ProductId=""BAD3"" DisplayName=""unknown kind"">
                <FanControl>Smm</FanControl>
            </Model>
            <!-- a BIOS-only board with a verified mode register -->
            <Model ProductId=""8BAC"" DisplayName=""BIOS-only board with OGHM"">
                <FanControl>Bios</FanControl>
                <TempCpuReg>178</TempCpuReg>
                <TempGpuReg>180</TempGpuReg>
                <ModeReg Verified=""true"">89</ModeReg>
            </Model>
            <!-- an unverified mode register on a BIOS-only board is ignored -->
            <Model ProductId=""BIO2"" DisplayName=""BIOS-only board, unverified mode register"">
                <FanControl>Bios</FanControl>
                <ModeReg>149</ModeReg>
            </Model>
            <Model ProductId=""BAD4"" DisplayName=""Verified is not a bool"">
                <FanControl>Bios</FanControl>
                <ModeReg Verified=""maybe"">89</ModeReg>
            </Model>
        </Models>
    </Config>
</OmenMon>
";

        private static string Norm(string text) {
            return text.Replace("\r\n", "\n").TrimEnd();
        }

        private static void AssertBios8C77(PlatformPreset p) {
            Assert.Equal(FanControlKind.Bios, p.FanControl);
            Assert.True(p.IsBiosOnly);
            Assert.Equal(178, p.TempCpuReg);
            Assert.Equal(180, p.TempGpuReg);
            Assert.Equal("BIOS-only board", p.DisplayName);
        }

        // T6: loads, saves and loads again unchanged
        [Fact]
        public void BiosModel_WithoutEcRegisters_LoadsSavesAndLoadsAgain() {
            File.WriteAllText(File_, Fixture);
            Config.Load();
            Assert.False(Config.LoadFailed);
            AssertBios8C77(Config.Models["8C77"]);
            Assert.Equal(FanControlKind.Ec, Config.Models["EC01"].FanControl);
            Assert.Equal(244, Config.Models["EC01"].SwitchReg);
            Assert.False(Config.Models["EC01"].ModeRegVerified);
            Assert.True(Config.Models["8BAC"].ModeRegVerified);
            Assert.Equal(0x59, Config.Models["8BAC"].ModeReg);
            Assert.False(Config.Models["BIO2"].ModeRegVerified);
            Assert.Equal(0, Config.Models["BIO2"].ModeReg);

            // Unchanged: the file stays as it is
            Config.Save();
            Assert.Equal(Norm(Fixture), Norm(File.ReadAllText(File_)));

            // Changed in memory (the unknown-model prompt's save): written as a BIOS-only
            // entry, without a single EC fan register, and it loads back the same
            Config.Models["8C77"].DisplayName = "renamed";
            Config.Save();
            string saved = File.ReadAllText(File_);
            string entry = Regex.Match(saved, @"<Model ProductId=""8C77""[\s\S]*?</Model>").Value;
            Assert.Contains("<FanControl>Bios</FanControl>", entry);
            foreach(string reg in new[] { "FanLevelReg", "FanRate", "FanSpeedReg", "CountdownReg", "ManualReg", "ModeReg", "SwitchReg" })
                Assert.DoesNotContain(reg, entry);

            // The verified mode register is saved with its mark
            Config.Models["8BAC"].DisplayName = "renamed too";
            Config.Save();
            string oghm = Regex.Match(File.ReadAllText(File_), @"<Model ProductId=""8BAC""[\s\S]*?</Model>").Value;
            Assert.Contains("<ModeReg Verified=\"true\">89</ModeReg>", oghm);
            Config.Models["8BAC"].DisplayName = "BIOS-only board with OGHM";
            Config.Save();
            saved = File.ReadAllText(File_);

            Config.Models = new Dictionary<string, PlatformPreset>(StringComparer.OrdinalIgnoreCase);
            Config.Load();
            Assert.True(Config.Models["8BAC"].ModeRegVerified);
            PlatformPreset again = Config.Models["8C77"];
            Assert.Equal("renamed", again.DisplayName);
            again.DisplayName = "BIOS-only board";
            AssertBios8C77(again);
            Config.Save();
            Assert.Equal(Norm(saved.Replace("renamed", "BIOS-only board")), Norm(File.ReadAllText(File_)));
        }

        // A malformed entry is not loaded, the reason is recorded (for -Diag), and the
        // file keeps the entry; nothing is read as 0
        [Fact]
        public void MalformedModels_AreNotLoaded_AndSayWhy() {
            File.WriteAllText(File_, Fixture);
            Config.Load();
            Assert.False(Config.Models.ContainsKey("BAD1"));
            Assert.False(Config.Models.ContainsKey("BAD2"));
            Assert.False(Config.Models.ContainsKey("BAD3"));
            Assert.False(Config.Models.ContainsKey("BAD4"));
            Assert.Contains(Config.ModelLoadProblems, p => p.Contains("BAD1") && p.Contains("SwitchReg") && p.Contains("missing"));
            Assert.Contains(Config.ModelLoadProblems, p => p.Contains("BAD2") && p.Contains("FanLevelReg1") && p.Contains("0xZZ"));
            Assert.Contains(Config.ModelLoadProblems, p => p.Contains("BAD3") && p.Contains("Smm"));
            Assert.Contains(Config.ModelLoadProblems, p => p.Contains("BAD4") && p.Contains("Verified") && p.Contains("maybe"));
            Assert.Equal(4, Config.ModelLoadProblems.Count);

            Config.Save();
            string text = File.ReadAllText(File_);
            Assert.Contains("ProductId=\"BAD1\"", text);
            Assert.Contains("<FanLevelReg1>0xZZ</FanLevelReg1>", text);
        }

        // The shipped OmenMon.xml, loaded as the application loads it
        internal static string ShippedConfig() {
            string root = AppContext.BaseDirectory;
            while(!File.Exists(Path.Combine(root, "OmenMon.xml")))
                root = Path.GetDirectoryName(root)!;
            return Path.Combine(root, "OmenMon.xml");
        }

        // Every shipped model loads (none dropped), and 8C77 is BIOS-only with its CPU and
        // GPU registers (0xB2, 0xB4)
        [Fact]
        public void ShippedModels_LoadWithoutProblems_8C77IsBiosOnly() {
            File.Copy(ShippedConfig(), File_, true);
            Config.Load();
            Assert.False(Config.LoadFailed);
            Assert.Empty(Config.ModelLoadProblems);
            PlatformPreset p = Config.Models["8C77"];
            Assert.Equal(FanControlKind.Bios, p.FanControl);
            Assert.Equal(0xB2, p.TempCpuReg);
            Assert.Equal(0xB4, p.TempGpuReg);
            Assert.True(AutoCal.IsKnownBoard("8C77"));
        }

        // T2: the shipped 8BAC entry is BIOS-only (temperature registers kept: CPU 0xB2,
        // GPU 0xB4), and a whole GUI session on it, with the shipped configuration, writes
        // nothing to an 8BAC-like EC (the measured 0x62 = 0x16, 0x63 = 0x20, the SMBus
        // buffer after it, 0x59 = 0x01) whether 0xF4 reads 0x00 (measured) or 0x02
        [Theory]
        [InlineData((byte) 0x00)]
        [InlineData((byte) 0x02)]
        public void Shipped8Bac_IsBiosOnly_GuiSessionWritesNothingToTheEc(byte switchByte) {
            File.Copy(ShippedConfig(), File_, true);
            Config.Load();
            Assert.Empty(Config.ModelLoadProblems);
            PlatformPreset p = Config.Models["8BAC"];
            Assert.Equal(FanControlKind.Bios, p.FanControl);
            Assert.Equal(0xB2, p.TempCpuReg);
            Assert.Equal(0xB4, p.TempGpuReg);
            Assert.True(p.ModeRegVerified);
            Assert.Equal(0x59, p.ModeReg);

            using var rig = new TestRig(preset: p);
            BiosOnlySessionTests.Fill8C77Image(rig.Ec, switchByte);
            rig.Ec.Data[0x59] = 0x01;
            rig.Bios.OnFanMode = m => rig.Ec.Data[0x59] = m;   // GC1A: OGHM = data[1]
            rig.SetCpuTemperature(45);
            rig.Ec.GpuTemperature = 48;
            byte[] image = (byte[]) rig.Ec.Data.Clone();
            rig.Ec.Reads.Clear();

            List<(int, int)> coolOff = BiosOnlySessionTests.RunSession(new GuiSession(rig));

            // 0x59 changes only through the BIOS (0x1A), never by an EC write; the mode it
            // held at the start (0x01) is replayed from it
            image[FakeEcRegisters.CpuTempRegister] = rig.Ec.Data[FakeEcRegisters.CpuTempRegister];
            image[FakeEcRegisters.GpuTempRegister] = rig.Ec.Data[FakeEcRegisters.GpuTempRegister];
            image[0x59] = rig.Ec.Data[0x59];
            BiosOnlySessionTests.AssertBiosOnly(rig, image, coolOff, new byte[] { 0x01 },
                FakeEcRegisters.CpuTempRegister, FakeEcRegisters.GpuTempRegister, 0x59);
            Assert.Contains(rig.Bios.Calls, c => c.Type == 0x1A && c.Data[1] == 0x01);
        }

        // T4: what the unknown-model prompt saves (Config.SaveModel of the detector's
        // result) has no control register, and loads back as a BIOS-only model
        [Fact]
        public void UnknownModelPrompt_SavesNoControlRegister() {
            File.WriteAllText(File_, Fixture);
            Config.Load();
            byte[] ec = new byte[256];
            ec[0x57] = 45;   // a plausible 2022 layout
            ec[0x34] = 20;
            PlatformPreset detected = AutoDetector.Detect("9ZZZ", ec)!;
            Assert.NotNull(detected);
            Config.SaveModel(detected);

            string entry = Regex.Match(File.ReadAllText(File_), @"<Model ProductId=""9ZZZ""[\s\S]*?</Model>").Value;
            Assert.Contains("<FanControl>Bios</FanControl>", entry);
            foreach(string reg in new[] { "FanLevelReg", "FanRate", "FanSpeedReg", "CountdownReg", "ManualReg", "ModeReg", "SwitchReg", "TempCpuReg", "TempGpuReg" })
                Assert.DoesNotContain(reg, entry);

            Config.Models = new Dictionary<string, PlatformPreset>(StringComparer.OrdinalIgnoreCase);
            Config.Load();
            Assert.True(Config.Models["9ZZZ"].IsBiosOnly);
        }

        // A BIOS-only model loaded from the configuration builds a fan array without an EC
        // fan register
        [Fact]
        public void BiosModel_FromConfig_BuildsABiosOnlyFanArray() {
            File.WriteAllText(File_, Fixture);
            Config.Load();
            using var rig = new TestRig(preset: Config.Models["8C77"]);
            Assert.Equal(FanControlKind.Bios, rig.Platform.Fans.Control);
            Assert.Equal(FanControlKind.Bios, rig.Platform.FanControl);
        }

    }

}
