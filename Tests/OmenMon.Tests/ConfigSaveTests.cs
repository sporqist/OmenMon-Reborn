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

    }

}
