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

            Config.Models = new Dictionary<string, PlatformPreset>(StringComparer.OrdinalIgnoreCase);
            Config.Load();
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
            Assert.Contains(Config.ModelLoadProblems, p => p.Contains("BAD1") && p.Contains("SwitchReg") && p.Contains("missing"));
            Assert.Contains(Config.ModelLoadProblems, p => p.Contains("BAD2") && p.Contains("FanLevelReg1") && p.Contains("0xZZ"));
            Assert.Contains(Config.ModelLoadProblems, p => p.Contains("BAD3") && p.Contains("Smm"));
            Assert.Equal(3, Config.ModelLoadProblems.Count);

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

        // T7: no BIOS fan mode is ever derived from the mode read back: no SetMode(...)
        // with GetMode() in its argument, and no value read with GetMode() (a variable or
        // field assigned from it) passed to SetMode or kept to restore later
        [Fact]
        public void Sources_NeverSendAModeReadBack() {
            string root = AppContext.BaseDirectory;
            while(!File.Exists(Path.Combine(root, "OmenMon.xml")))
                root = Path.GetDirectoryName(root)!;
            Func<string, string> code = f => Regex.Replace(File.ReadAllText(f), @"//.*", "");

            var files = new[] { "App", "Core", "Hardware", "Library" }
                .Select(d => Path.Combine(root, d))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .ToList();
            Assert.Contains(files, f => f.EndsWith("FanProgram.cs"));

            var problems = new List<string>();
            int setModeCalls = 0;
            foreach(string file in files) {
                string text = code(file);
                string name = Path.GetRelativePath(root, file);

                // Every SetMode( call and its argument (up to the matching parenthesis)
                foreach(Match m in Regex.Matches(text, @"\bSetMode\s*\(")) {
                    int depth = 1, i = m.Index + m.Length;
                    while(i < text.Length && depth > 0) {
                        if(text[i] == '(') depth++;
                        else if(text[i] == ')') depth--;
                        i++;
                    }
                    string arg = text.Substring(m.Index + m.Length, Math.Max(0, i - 1 - (m.Index + m.Length)));
                    setModeCalls++;
                    if(arg.Contains("GetMode("))
                        problems.Add(name + ": SetMode(" + arg.Trim() + ")");
                }

                // Whatever is assigned from GetMode() must not reach SetMode, nor be kept
                // as the mode to restore
                foreach(Match m in Regex.Matches(text, @"([\w\.]+)\s*=\s*[^;=]*\bGetMode\s*\(")) {
                    string target = m.Groups[1].Value;
                    string last = target.Substring(target.LastIndexOf('.') + 1);
                    if(last == "Mode" && Regex.IsMatch(target, @"^s\.Mode$"))
                        continue;  // the GUI snapshot's display value (checked below)
                    if(Regex.IsMatch(text, @"\bSetMode\s*\([^;]*\b" + Regex.Escape(last) + @"\b")
                        || Regex.IsMatch(last, "Last|Prior|Saved|Restore", RegexOptions.IgnoreCase))
                        problems.Add(name + ": " + m.Value.Trim());
                }
            }

            Assert.True(setModeCalls >= 5, setModeCalls + " SetMode calls found: is the scan still reading the sources?");
            Assert.Empty(problems);

            // The snapshot's mode is displayed and never sent: no SetMode in the GUI
            // takes a snapshot's mode
            foreach(string file in files.Where(f => f.Contains(Path.DirectorySeparatorChar + "Gui" + Path.DirectorySeparatorChar)))
                Assert.DoesNotMatch(@"\bSetMode\s*\([^;]*\.Mode\b", code(file));
        }

    }

}
