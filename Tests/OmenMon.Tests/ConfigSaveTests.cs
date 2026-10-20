  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System.Reflection;
using System.Xml;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // Saving the configuration updates the user's file instead of regenerating it,
    // and loading keeps defaults when a value is missing or out of range.
    [Collection(HardwareStateCollection.Name)]
    public class ConfigSaveTests : IDisposable {

        private readonly string Dir;
        private readonly string File_;
        private readonly Dictionary<FieldInfo, object?> Saved = new();

        public ConfigSaveTests() {
            Config.LocaleInit();

            // Every static setting is restored afterwards
            foreach(FieldInfo f in typeof(Config).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                if(!f.IsLiteral && !f.IsInitOnly)
                    Saved[f] = f.GetValue(null);

            // Load() adds to these in place, so work on copies
            Config.FanProgram = new SortedList<string, FanProgramData>();
            Config.Models = new Dictionary<string, PlatformPreset>(StringComparer.OrdinalIgnoreCase);
            Config.ColorPreset = new SortedDictionary<string, BiosData.ColorTable>(Config.ColorPreset);
            Config.TemperatureSensor = new Dictionary<string, Config.TemperatureSensorData>(Config.TemperatureSensor);

            Dir = Path.Combine(Path.GetTempPath(), "omenmon-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            File_ = Path.Combine(Dir, "OmenMon.xml");
            Config.FilePath = File_;
        }

        public void Dispose() {
            foreach(KeyValuePair<FieldInfo, object?> pair in Saved)
                pair.Key.SetValue(null, pair.Value);
            try { Directory.Delete(Dir, true); } catch { }
        }

        private const string Fixture = @"<?xml version=""1.0"" encoding=""utf-8""?>
<OmenMon>
    <!-- user comment at the top -->
    <Config>
        <!-- comment before a value -->
        <AutoConfig>false</AutoConfig>
        <FutureSetting Kind=""unknown"">keep me</FutureSetting>
        <EcRetryLimit>0</EcRetryLimit>
        <UpdateProgramInterval>0</UpdateProgramInterval>
        <FanLevelMax>55</FanLevelMax>
        <FanLevelMin>70</FanLevelMin>
        <GpuPowerDefault>Bogus</GpuPowerDefault>
        <FanPrograms Note=""parent attribute"">
            <Program Name=""Alpha"">
                <!-- inside Alpha -->
                <FanMode>Default</FanMode>
                <GpuPower>Medium</GpuPower>
                <Level Temperature=""00""><Cpu>20</Cpu><Gpu>22</Gpu></Level>
                <Level Temperature=""60""><Cpu>35</Cpu><Gpu>35</Gpu></Level>
            </Program>
            <!-- between programs -->
            <Program Name=""Beta"">
                <FanMode>Performance</FanMode>
                <GpuPower>Maximum</GpuPower>
                <Level Temperature=""00""><Cpu>30</Cpu><Gpu>30</Gpu></Level>
            </Program>
            <Program Name=""Broken"">
                <FanMode>NoSuchMode</FanMode>
                <GpuPower>Maximum</GpuPower>
                <Level Temperature=""00""><Cpu>30</Cpu><Gpu>30</Gpu></Level>
            </Program>
        </FanPrograms>
        <Models>
            <Model ProductId=""8bac"" DisplayName=""Test board"">
                <!-- inside the model -->
                <FanLevelReg0>52</FanLevelReg0><FanLevelReg1>53</FanLevelReg1>
                <FanRateReadReg0>46</FanRateReadReg0><FanRateReadReg1>47</FanRateReadReg1>
                <FanRateWriteReg0>44</FanRateWriteReg0><FanRateWriteReg1>45</FanRateWriteReg1>
                <FanSpeedReg0>69</FanSpeedReg0><FanSpeedReg1>71</FanSpeedReg1>
                <CountdownReg>99</CountdownReg><ManualReg>98</ManualReg>
                <ModeReg>149</ModeReg><SwitchReg>244</SwitchReg>
            </Model>
            <Model ProductId=""9999"">
                <FanLevelReg0>not a number</FanLevelReg0>
            </Model>
        </Models>
    </Config>
</OmenMon>
";

        private XmlDocument SaveAndRead() {
            Config.SaveCore();
            XmlDocument xml = new XmlDocument();
            xml.Load(File_);
            return xml;
        }

        private static XmlElement? Program(XmlDocument xml, string name) {
            return xml.SelectSingleNode("OmenMon/Config/FanPrograms/Program[@Name='" + name + "']") as XmlElement;
        }

        [Fact]
        public void Save_KeepsCommentsUnknownElementsAndEntriesThatFailedToLoad() {
            File.WriteAllText(File_, Fixture);
            Config.Load();
            Assert.False(Config.FanProgram.ContainsKey("Broken"));

            XmlDocument xml = SaveAndRead();
            string text = File.ReadAllText(File_);
            foreach(string comment in new[] { "user comment at the top", "comment before a value",
                "inside Alpha", "between programs", "inside the model" })
                Assert.Contains(comment, text);
            Assert.Equal("keep me", xml.SelectSingleNode("OmenMon/Config/FutureSetting")!.InnerText);
            Assert.Equal("parent attribute", ((XmlElement) xml.SelectSingleNode("OmenMon/Config/FanPrograms")!).GetAttribute("Note"));
            Assert.Equal("NoSuchMode", Program(xml, "Broken")!["FanMode"]!.InnerText);
            Assert.NotNull(xml.SelectSingleNode("OmenMon/Config/Models/Model[@ProductId='9999']"));
            Assert.Single(xml.SelectNodes("OmenMon/Config/Models/Model[@ProductId='8bac']")!.Cast<XmlNode>());
            Assert.False(File.Exists(File_ + ".tmp"));
        }

        [Fact]
        public void Save_RemovesDeleted_AppendsNew_RewritesOnlyChanged() {
            File.WriteAllText(File_, Fixture);
            Config.Load();

            Config.FanProgram.Remove("Beta");
            FanProgramData alpha = Config.FanProgram["Alpha"];
            Config.FanProgram["Gamma"] = new FanProgramData("Gamma", BiosData.FanMode.Default, BiosData.GpuPowerLevel.Med,
                new SortedDictionary<byte, byte[]> { [0] = new byte[] { 25, 25 } });

            XmlDocument xml = SaveAndRead();
            Assert.Null(Program(xml, "Beta"));
            Assert.NotNull(Program(xml, "Gamma"));
            Assert.NotNull(Program(xml, "Broken"));
            Assert.Contains("inside Alpha", Program(xml, "Alpha")!.InnerXml); // unchanged, untouched

            // A changed entry is rewritten
            alpha.Level[60] = new byte[] { 40, 40 };
            xml = SaveAndRead();
            Assert.Equal("40", Program(xml, "Alpha")!.SelectSingleNode("Level[@Temperature='60']/Cpu")!.InnerText);

            // Deleting something added in this session works too
            Config.FanProgram.Remove("Gamma");
            xml = SaveAndRead();
            Assert.Null(Program(xml, "Gamma"));
        }

        [Fact]
        public void Save_UnreadableFile_IsKeptAsBackup() {
            const string garbage = "<OmenMon><Config><AutoConfig>true</Auto";
            File.WriteAllText(File_, garbage);
            Config.SaveCore();
            string[] backups = Directory.GetFiles(Dir, "OmenMon.xml.unreadable-*.bak");
            Assert.Single(backups);
            Assert.Equal(garbage, File.ReadAllText(backups[0]));
            new XmlDocument().Load(File_); // the new file parses
        }

        [Fact]
        public void Save_ShippedConfig_RoundTripLosesNothing() {
            string root = AppContext.BaseDirectory;
            while(!File.Exists(Path.Combine(root, "OmenMon.xml")))
                root = Path.GetDirectoryName(root)!;
            File.Copy(Path.Combine(root, "OmenMon.xml"), File_);
            XmlDocument before = new XmlDocument();
            before.Load(File_);
            Config.Load();

            XmlDocument after = SaveAndRead();
            Assert.Equal(before.SelectNodes("//comment()")!.Count, after.SelectNodes("//comment()")!.Count);
            foreach(string path in new[] { "OmenMon/Config/FanPrograms/Program", "OmenMon/Config/Models/Model",
                "OmenMon/Config/Temperature/Sensor", "OmenMon/Config/ColorPresets/Preset" })
                Assert.Equal(before.SelectNodes(path)!.Count, after.SelectNodes(path)!.Count);
        }

        [Fact]
        public void Load_MissingOrInvalidValues_KeepDefaults() {
            Config.GpuPowerDefault = "Maximum";
            Config.FanProgramDefault = "Power";
            Config.KeyCustomActionExecCmd = "calc.exe";
            Config.EcRetryLimit = 3;
            Config.UpdateProgramInterval = 15;
            Config.FanLevelMax = 55;
            Config.FanLevelMin = 20;
            File.WriteAllText(File_, Fixture);
            Config.Load();

            Assert.Equal("Maximum", Config.GpuPowerDefault);      // "Bogus"
            Assert.Equal("Power", Config.FanProgramDefault);      // missing
            Assert.Equal("calc.exe", Config.KeyCustomActionExecCmd); // missing
            Assert.Equal(3, Config.EcRetryLimit);                 // 0
            Assert.Equal(15, Config.UpdateProgramInterval);       // 0
            Assert.Equal(20, Config.FanLevelMin);                 // 70 > FanLevelMax
            Assert.False(Config.AutoConfig);                      // valid values still load
        }

        [Fact]
        public void UnsetDefaultProgram_DoesNotThrow() {
            // A configuration without <FanProgramDefault> leaves it null
            var rig = new TestRig();
            Config.FanProgramDefault = null;
            var program = new FanProgram(rig.Platform, (s, m) => { });
            Assert.False(program.Run(Config.FanProgramDefault));
            Assert.Empty(rig.Bios.LevelWrites);
        }

        [Fact]
        public void Load_ModelKeys_AreCaseInsensitive_AndSaveDoesNotDuplicate() {
            File.WriteAllText(File_, Fixture);
            Config.Load();
            Assert.True(Config.Models.ContainsKey("8BAC"));
            Config.Models["8BAC"] = Config.Models["8BAC"]; // what SaveModel() does before saving
            Config.SaveCore();
            XmlDocument xml = new XmlDocument();
            xml.Load(File_);
            Assert.Single(xml.SelectNodes("OmenMon/Config/Models/Model")!.Cast<XmlNode>(),
                n => ((XmlElement) n).GetAttribute("ProductId").Equals("8bac", StringComparison.OrdinalIgnoreCase));
        }

        // A board entry with one child missing (no <SwitchReg>), as a hand edit might leave it
        private const string BrokenModel = @"<Model ProductId=""8BAC"" DisplayName=""Measured""><!-- hand-measured -->"
            + "<FanLevelReg0>52</FanLevelReg0><FanLevelReg1>53</FanLevelReg1><FanRateReadReg0>46</FanRateReadReg0>"
            + "<FanRateReadReg1>47</FanRateReadReg1><FanRateWriteReg0>58</FanRateWriteReg0><FanRateWriteReg1>59</FanRateWriteReg1>"
            + "<FanSpeedReg0>176</FanSpeedReg0><FanSpeedReg1>178</FanSpeedReg1><CountdownReg>99</CountdownReg>"
            + "<ManualReg>98</ManualReg><ModeReg>149</ModeReg></Model>";

        private static XmlNodeList Models(XmlDocument xml, string id) {
            return xml.SelectNodes("OmenMon/Config/Models/Model[@ProductId='" + id + "']")!;
        }

        [Fact]
        public void Save_ModelThatFailedToLoad_IsNeverReplaced() {
            File.WriteAllText(File_, Fixture.Replace(@"<Model ProductId=""9999"">", BrokenModel + @"<Model ProductId=""9999"">")
                .Replace(@"ProductId=""8bac""", @"ProductId=""8BAB"""));
            Config.Load();
            Assert.False(Config.Models.ContainsKey("8BAC"));
            XmlDocument before = new XmlDocument();
            before.Load(File_);
            string broken = Models(before, "8BAC")[0]!.OuterXml;

            // What SaveModel() does with a scan result
            PlatformPreset scanned = new PlatformPreset { ProductId = "8BAC", DisplayName = "Scanned",
                FanLevelReg0 = 17, FanLevelReg1 = 17, ModeReg = 89, SwitchReg = 244 };
            Config.Models["8BAC"] = scanned;
            XmlDocument xml = SaveAndRead();
            XmlNodeList list = Models(xml, "8BAC");
            Assert.Equal(2, list.Count);
            Assert.Equal(broken, list[0]!.OuterXml);
            Assert.Equal("Scanned", ((XmlElement) list[1]!).GetAttribute("DisplayName"));

            // Loading takes the valid one, and saving again leaves the broken one alone
            Config.Models.Clear();
            Config.Load();
            Assert.Equal("Scanned", Config.Models["8BAC"].DisplayName);
            Config.Models["8BAC"].ModeReg = 90;
            list = Models(SaveAndRead(), "8BAC");
            Assert.Equal(2, list.Count);
            Assert.Equal(broken, list[0]!.OuterXml);
            Assert.Equal("90", list[1]!["ModeReg"]!.InnerText);
        }

        [Fact]
        public void Save_BrokenDraftAfterTheLoadedEntry_IsKept() {
            // A revised copy of Alpha pasted below the working one, with a typo
            const string draft = @"<Program Name=""Alpha""><FanMode>Default</FanMode><GpuPower>Medum</GpuPower>"
                + @"<Level Temperature=""00""><Cpu>25</Cpu><Gpu>28</Gpu></Level></Program>";
            File.WriteAllText(File_, Fixture.Replace(@"<Program Name=""Broken"">", draft + @"<Program Name=""Broken"">"));
            Config.Load();
            Assert.Equal(20, Config.FanProgram["Alpha"].Level[0][0]);

            // Changed in the application: the loaded element is updated, the draft is kept
            Config.FanProgram["Alpha"].Level[60] = new byte[] { 40, 40 };
            XmlDocument xml = SaveAndRead();
            XmlNodeList list = xml.SelectNodes("OmenMon/Config/FanPrograms/Program[@Name='Alpha']")!;
            Assert.Equal(2, list.Count);
            Assert.Equal("40", list[0]!.SelectSingleNode("Level[@Temperature='60']/Cpu")!.InnerText);
            Assert.Equal("Medum", list[1]!["GpuPower"]!.InnerText);

            // Deleted in the application: only the element that loaded is removed
            Config.FanProgram.Remove("Alpha");
            list = SaveAndRead().SelectNodes("OmenMon/Config/FanPrograms/Program[@Name='Alpha']")!;
            Assert.Single(list.Cast<XmlNode>());
            Assert.Equal("Medum", list[0]!["GpuPower"]!.InnerText);
        }

        [Fact]
        public void Save_SensorsThatDidNotLoad_AreNotReplacedByTheDefaults() {
            // No sensor is set to be used, so loading keeps the defaults
            File.WriteAllText(File_, Fixture.Replace("<Models>",
                @"<Temperature><Sensor Name=""CPUT"" Source=""EC"" Use=""false"" /><Sensor Name=""BIOS"" Source=""BIOS"" Use=""false"" /></Temperature><Models>"));
            Config.Load();
            Assert.True(Config.TemperatureSensor["CPUT"].Use);
            XmlDocument xml = SaveAndRead();
            XmlNodeList sensors = xml.SelectNodes("OmenMon/Config/Temperature/Sensor")!;
            Assert.Equal(2, sensors.Count);
            Assert.All(sensors.Cast<XmlElement>(), s => Assert.Equal("false", s.GetAttribute("Use")));
        }

        private static string? Value(XmlDocument xml, string name) {
            return xml.SelectSingleNode("OmenMon/Config/" + name)?.InnerText;
        }

        // Replaces one value in the file, as a hand edit in an editor would
        private void EditFile(string name, string value) {
            XmlDocument xml = new XmlDocument();
            xml.Load(File_);
            XmlNode? node = xml.SelectSingleNode("OmenMon/Config/" + name);
            if(node == null)
                node = xml.SelectSingleNode("OmenMon/Config")!.AppendChild(xml.CreateElement(name))!;
            node.InnerText = value;
            xml.Save(File_);
        }

        private const string ScalarFixture = @"<?xml version=""1.0"" encoding=""utf-8""?>
<OmenMon>
    <Config>
        <!-- Fan safety -->
        <FanSafetyFloorCpu>25</FanSafetyFloorCpu>
        <FanSafetyFloorGpu>22</FanSafetyFloorGpu>
        <FanSafetyWarmCurve>60:30/33,70:40/43,80:60/63</FanSafetyWarmCurve>
        <!-- Tip -->
        <GuiTipDuration>30000</GuiTipDuration>
        <GuiStayOnTop>true</GuiStayOnTop>
        <EcRetryLimit>0</EcRetryLimit>
        <AutoStartup Note=""mine"">false</AutoStartup>
        <BiosErrorReporting><!-- keep -->true</BiosErrorReporting>
        <EcFailLimit>15</EcFailLimit>
        <EcFailLimit>20</EcFailLimit>
        <KeyCustomAction>
            <Enabled>false</Enabled>
            <ExecCmd></ExecCmd>
        </KeyCustomAction>
    </Config>
</OmenMon>
";

        [Fact]
        public void Save_DefaultsInTheFile_AreRemoved_OtherValuesAndCommentsStay() {
            File.WriteAllText(File_, ScalarFixture);
            Config.Load();
            XmlDocument xml = SaveAndRead();

            // Fan safety follows the defaults unless set otherwise
            Assert.Equal("25", Value(xml, "FanSafetyFloorCpu"));
            Assert.Null(Value(xml, "FanSafetyFloorGpu"));
            Assert.Null(Value(xml, "FanSafetyWarmCurve"));
            Assert.Null(Value(xml, "GuiTipDuration"));
            Assert.Null(xml.SelectSingleNode("OmenMon/Config/KeyCustomAction")); // emptied
            Assert.Equal("true", Value(xml, "GuiStayOnTop"));
            Assert.Equal("0", Value(xml, "EcRetryLimit"));          // invalid, not loaded: left alone
            Assert.Equal("false", Value(xml, "AutoStartup"));       // has an attribute
            Assert.NotNull(xml.SelectSingleNode("OmenMon/Config/BiosErrorReporting/comment()"));
            Assert.Equal(2, xml.SelectNodes("OmenMon/Config/EcFailLimit")!.Count); // the second would load
            string text = File.ReadAllText(File_);
            Assert.Contains("Fan safety", text);
            Assert.Contains("Tip", text);

            // Nothing at its default is added
            Assert.Null(Value(xml, "AutoConfig"));
            Assert.Null(Value(xml, "ThermalPanicTemperature"));
            Assert.Null(xml.SelectSingleNode("OmenMon/CustomPreset"));

            // What remains loads as before
            Config.FanSafetyFloorCpu = 20;
            Config.GuiStayOnTop = false;
            Config.Load();
            Assert.Equal(25, Config.FanSafetyFloorCpu);
            Assert.Equal(22, Config.FanSafetyFloorGpu);
            Assert.True(Config.GuiStayOnTop);
        }

        [Fact]
        public void Save_ChangedValues_AreWritten_OrRemovedWhenBackAtTheDefault() {
            File.WriteAllText(File_, ScalarFixture);
            Config.Load();
            Config.FanSafetyFloorCpu = 20;      // back to the default
            Config.FanSafetyFloorGpu = 30;
            Config.EcoPlusEnabled = true;       // not in the file yet
            Config.FanProgramDefault = "Quiet";
            XmlDocument xml = SaveAndRead();
            Assert.Null(Value(xml, "FanSafetyFloorCpu"));
            Assert.Equal("30", Value(xml, "FanSafetyFloorGpu"));
            Assert.Equal("true", Value(xml, "EcoPlusEnabled"));
            Assert.Equal("Quiet", Value(xml, "FanProgramDefault"));

            Config.FanSafetyFloorGpu = 22;
            Config.EcoPlusEnabled = false;
            xml = SaveAndRead();
            Assert.Null(Value(xml, "FanSafetyFloorGpu"));
            Assert.Null(Value(xml, "EcoPlusEnabled"));
        }

        [Fact]
        public void Save_HandEditSinceLoad_IsNotReverted() {
            File.WriteAllText(File_, ScalarFixture);
            Config.Load();

            // Edited while the application runs, then any setting is changed in it
            EditFile("FanSafetyFloorCpu", "30");
            EditFile("FanSafetyWarmCurve", "60:35/38");
            EditFile("FanProgramDefault", "Venue");
            Config.TemperatureUseFahrenheit = true;
            XmlDocument xml = SaveAndRead();
            Assert.Equal("30", Value(xml, "FanSafetyFloorCpu"));
            Assert.Equal("60:35/38", Value(xml, "FanSafetyWarmCurve"));
            Assert.Equal("Venue", Value(xml, "FanProgramDefault"));
            Assert.Equal("true", Value(xml, "TemperatureUseFahrenheit"));

            // A value this session saved is the new baseline: a later edit survives too
            EditFile("TemperatureUseFahrenheit", "false");
            EditFile("UpdateIconInterval", "5");
            Config.GuiDynamicIcon = true;
            xml = SaveAndRead();
            Assert.Equal("5", Value(xml, "UpdateIconInterval"));

            // An edit back to the default is removed as a default, which loads the same
            Assert.Null(Value(xml, "TemperatureUseFahrenheit"));

            // A setting changed in the application wins over an edit of the same setting
            Config.GuiTipDuration = 5000;
            EditFile("GuiTipDuration", "10000");
            xml = SaveAndRead();
            Assert.Equal("5000", Value(xml, "GuiTipDuration"));
        }

        [Fact]
        public void Save_EntryEditedByHandSinceLoad_IsNotReverted() {
            File.WriteAllText(File_, Fixture);
            Config.Load();

            // The lowest row of Alpha and a model register edited while the application runs
            string text = File.ReadAllText(File_)
                .Replace("<Cpu>20</Cpu><Gpu>22</Gpu>", "<Cpu>22</Cpu><Gpu>25</Gpu>")
                .Replace("<ModeReg>149</ModeReg>", "<ModeReg>150</ModeReg>");
            File.WriteAllText(File_, text);

            // Then something else is changed in the application
            Config.FanProgram["Beta"].Level[0] = new byte[] { 33, 33 };
            XmlDocument xml = SaveAndRead();
            Assert.Equal("22", Program(xml, "Alpha")!.SelectSingleNode("Level[@Temperature='00']/Cpu")!.InnerText);
            Assert.Contains("inside Alpha", Program(xml, "Alpha")!.InnerXml);
            Assert.Equal("150", Models(xml, "8bac")[0]!["ModeReg"]!.InnerText);
            Assert.Equal("33", Program(xml, "Beta")!.SelectSingleNode("Level[@Temperature='00']/Cpu")!.InnerText);
        }

        [Fact]
        public void Save_DefaultEntries_AreNotWritten_UntilTheCollectionChanges() {
            File.WriteAllText(File_, Fixture);
            Config.Load();
            XmlDocument xml = SaveAndRead();
            Assert.Null(xml.SelectSingleNode("OmenMon/Config/ColorPresets"));
            Assert.Null(xml.SelectSingleNode("OmenMon/Config/Temperature"));

            // Loading replaces all default presets with those in the file, so the first
            // preset saved takes the unchanged defaults along
            Config.ColorPreset["Mine"] = new BiosData.ColorTable("FF0000:FF0000:FF0000:FF0000");
            xml = SaveAndRead();
            Assert.Equal(3, xml.SelectNodes("OmenMon/Config/ColorPresets/Preset")!.Count);
            Config.ColorPreset = new SortedDictionary<string, BiosData.ColorTable>();
            Config.Load();
            Assert.Equal(new[] { "DefaultApp", "DefaultOem", "Mine" }, Config.ColorPreset.Keys.ToArray());
        }

        private static void ResetCustomPreset() {
            Config.CustomPresetFanMode = "Default";
            Config.CustomPresetGpuPower = "Medium";
            Config.CustomPresetMaxRpm = 0;
            Config.CustomPresetCpuLevel = 0;
            Config.CustomPresetGpuLevel = 0;
        }

        private static void AssertCustomPreset(string mode, string power, int maxRpm, byte cpu, byte gpu) {
            Assert.Equal(mode, Config.CustomPresetFanMode);
            Assert.Equal(power, Config.CustomPresetGpuPower);
            Assert.Equal(maxRpm, Config.CustomPresetMaxRpm);
            Assert.Equal(cpu, Config.CustomPresetCpuLevel);
            Assert.Equal(gpu, Config.CustomPresetGpuLevel);
        }

        [Fact]
        public void CustomPreset_SurvivesSaveAndLoad() {
            File.WriteAllText(File_, Fixture);
            Config.Load();
            Config.CustomPresetFanMode = "Performance";
            Config.CustomPresetGpuPower = "Maximum";
            Config.CustomPresetMaxRpm = 4500;
            Config.CustomPresetCpuLevel = 40;
            Config.CustomPresetGpuLevel = 43;

            XmlDocument xml = SaveAndRead();
            Assert.Equal("40", xml.SelectSingleNode("OmenMon/CustomPreset/Fan0Level")!.InnerText);
            Assert.Null(xml.SelectSingleNode("OmenMon/n"));

            ResetCustomPreset();
            Config.Load();
            AssertCustomPreset("Performance", "Maximum", 4500, 40, 43);
        }

        [Fact]
        public void CustomPreset_StrayElement_IsReadAndMoved() {
            // Where earlier versions saved it
            File.WriteAllText(File_, Fixture.Replace("</OmenMon>",
                "<n><CustomPreset><Mode>Performance</Mode><GpuPower>Minimum</GpuPower><MaxRpm>4000</MaxRpm>"
                + "<Fan0Level>30</Fan0Level><Fan1Level>33</Fan1Level></CustomPreset></n></OmenMon>"));
            ResetCustomPreset();
            Config.Load();
            AssertCustomPreset("Performance", "Minimum", 4000, 30, 33);

            XmlDocument xml = SaveAndRead();
            Assert.Null(xml.SelectSingleNode("OmenMon/n"));
            Assert.Equal("33", xml.SelectSingleNode("OmenMon/CustomPreset/Fan1Level")!.InnerText);
            ResetCustomPreset();
            Config.Load();
            AssertCustomPreset("Performance", "Minimum", 4000, 30, 33);
        }

        [Fact]
        public void CustomPreset_StrayElement_WithOtherContent_IsKept() {
            File.WriteAllText(File_, Fixture.Replace("</OmenMon>",
                "<n><Other>keep</Other><CustomPreset><!-- note --><Fan0Level>30</Fan0Level></CustomPreset></n></OmenMon>"));
            Config.Load();
            XmlDocument xml = SaveAndRead();
            Assert.Equal("keep", xml.SelectSingleNode("OmenMon/n/Other")!.InnerText);
            Assert.NotNull(xml.SelectSingleNode("OmenMon/n/CustomPreset/comment()"));
            Assert.Null(xml.SelectSingleNode("OmenMon/n/CustomPreset/Fan0Level"));
            Assert.Equal("30", xml.SelectSingleNode("OmenMon/CustomPreset/Fan0Level")!.InnerText);
        }

        [Fact]
        public void CustomPreset_CorrectLocation_WinsOverStray() {
            File.WriteAllText(File_, Fixture.Replace("</OmenMon>",
                "<CustomPreset><Fan0Level>45</Fan0Level></CustomPreset>"
                + "<n><CustomPreset><Fan0Level>30</Fan0Level></CustomPreset></n></OmenMon>"));
            ResetCustomPreset();
            Config.Load();
            Assert.Equal(45, Config.CustomPresetCpuLevel);
        }

    }

}
