  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System.Text;
using OmenMon.Library;

namespace OmenMon.Tests {

    // Graphics apps, the pure part: preference values, profiles, the planner, adopting
    // what is set now and reading a .reg export
    public class GraphicsCoreTests {

        private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        private const string TouchDesigner = @"C:\Program Files\Derivative\TouchDesigner\bin\TouchDesigner.exe";
        private const string Ableton = @"C:\ProgramData\Ableton\Live 12 Suite\Program\Ableton Live 12 Suite.exe";
        private const string NvContainer = @"C:\WINDOWS\System32\DriverStore\FileRepository\nvhmsi.inf_amd64_77e3e5801586496b\Display.NvContainer\NVDisplay.Container.exe";
        private const string Explorer = @"C:\WINDOWS\explorer.exe";
        private const string OneDrive = @"C:\Users\maroa\AppData\Local\Microsoft\OneDrive\OneDrive.exe";
        private const string Terminal = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";

#region Values
        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("GpuPreference=2;", "2")]
        [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=1;", "1")]
        [InlineData("SwapEffectUpgradeEnable=1;", null)]
        [InlineData(" gpupreference = 0 ;", "0")]
        [InlineData("GpuPreference=1", "1")]
        public void Token_IsRead(string? value, string? token) {
            Assert.Equal(token, GpuValue.Token(value!));
        }

        [Theory]
        [InlineData(null, GpuChoice.PowerSaving, "GpuPreference=1;")]
        [InlineData("", GpuChoice.HighPerformance, "GpuPreference=2;")]
        [InlineData("GpuPreference=2;", GpuChoice.PowerSaving, "GpuPreference=1;")]
        [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=2;AutoHDREnable=1;", GpuChoice.PowerSaving,
            "SwapEffectUpgradeEnable=1;GpuPreference=1;AutoHDREnable=1;")]
        [InlineData("SwapEffectUpgradeEnable=1;", GpuChoice.Auto, "SwapEffectUpgradeEnable=1;GpuPreference=0;")]
        [InlineData("A=1", GpuChoice.PowerSaving, "A=1;GpuPreference=1;")]
        [InlineData("GpuPreference=2;A=1;GpuPreference=2;", GpuChoice.PowerSaving, "GpuPreference=1;A=1;")]
        public void With_SetsTheToken_KeepingTheOtherPairsInPlace(string? value, GpuChoice choice, string expected) {
            Assert.Equal(expected, GpuValue.With(value!, choice));
        }

        [Fact]
        public void With_TheSameToken_LeavesTheValueAsItIs() {
            Assert.Equal("A=1;GpuPreference=1", GpuValue.With("A=1;GpuPreference=1", GpuChoice.PowerSaving));
        }

        [Theory]
        [InlineData("A=1;GpuPreference=1;B=2;", "A=1;B=2;")]
        [InlineData("GpuPreference=1;", "")]
        [InlineData("A=1", "A=1")]
        [InlineData(null, null)]
        public void WithoutToken_KeepsTheRest(string? value, string? expected) {
            Assert.Equal(expected, GpuValue.WithToken(value!, null!));
        }

        [Theory]
        [InlineData(null, "default")]
        [InlineData("SwapEffectUpgradeEnable=1;", "default")]
        [InlineData("GpuPreference=0;", "let Windows decide")]
        [InlineData("GpuPreference=1;", "power saving")]
        [InlineData("A=1;GpuPreference=2;", "high performance")]
        [InlineData("GpuPreference=7;", "GpuPreference=7")]
        public void Describe(string? value, string expected) {
            Assert.Equal(expected, GpuValue.Describe(value!));
        }

        [Fact]
        public void SameChoice_NoTokenIsLetWindowsDecide() {
            Assert.True(GpuValue.SameChoice(null!, "GpuPreference=0;"));
            Assert.True(GpuValue.SameChoice("A=1;", null!));
            Assert.True(GpuValue.SameChoice("A=1;GpuPreference=2;", "GpuPreference=2;B=0;"));
            Assert.False(GpuValue.SameChoice("GpuPreference=1;", null!));
            Assert.Null(GpuValue.Choice("GpuPreference=7;"));
            Assert.Equal(GpuChoice.Auto, GpuValue.Choice(null!));
        }
#endregion

#region Profiles
        [Fact]
        public void Profile_Set_KeepsPlaceAndSpelling_AndIgnoresCase() {
            var p = new GraphicsProfile("Show");
            p.Set(TouchDesigner, GpuChoice.HighPerformance);
            p.Set(Chrome, GpuChoice.PowerSaving);
            p.Set(TouchDesigner.ToUpperInvariant(), GpuChoice.Auto);
            Assert.Equal(2, p.Apps.Count);
            Assert.Equal(TouchDesigner, p.Apps[0].Key);
            Assert.Equal(GpuChoice.Auto, p.Get(TouchDesigner.ToLowerInvariant()));
            Assert.True(p.Remove(Chrome.ToUpperInvariant()));
            Assert.False(p.Contains(Chrome));
            Assert.Null(p.Get(Chrome));
            Assert.Throws<ArgumentException>(() => p.Set("a\tb.exe", GpuChoice.Auto));
            Assert.Throws<ArgumentException>(() => p.Set("a.exe", (GpuChoice) 7));
        }

        [Theory]
        [InlineData("Show", true)]
        [InlineData("Power saving", true)]
        [InlineData("", false)]
        [InlineData(" Show", false)]
        [InlineData("-Bios", false)]
        [InlineData("Default", false)]
        [InlineData("windows default", false)]
        [InlineData("A\tB", false)]
        public void Profile_Names(string name, bool valid) {
            Assert.Equal(valid, GraphicsProfile.IsValidName(name));
        }

        [Fact]
        public void Profile_Find_DefaultIsWindowsDefault() {
            var profiles = new SortedList<string, GraphicsProfile>(StringComparer.OrdinalIgnoreCase) { ["Show"] = new GraphicsProfile("Show") };
            Assert.True(GraphicsProfile.Find(profiles, "default", out GraphicsProfile? p));
            Assert.Null(p);
            Assert.True(GraphicsProfile.Find(profiles, "show", out p));
            Assert.Equal("Show", p!.Name);
            Assert.False(GraphicsProfile.Find(profiles, "Gig", out p));
            Assert.False(GraphicsProfile.Find(profiles, "", out p));
        }

        [Fact]
        public void AddApps_ToTheActiveProfile() {
            var show = new GraphicsProfile("Show");
            show.Set(TouchDesigner, GpuChoice.HighPerformance);
            show.Set(Chrome, GpuChoice.HighPerformance);
            var profiles = new SortedList<string, GraphicsProfile>(StringComparer.OrdinalIgnoreCase) { ["Show"] = show };
            var p = GraphicsProfile.AddApps(profiles, "Show", new[] { Chrome, Explorer }, GpuChoice.PowerSaving, null!);
            Assert.Same(show, p);
            Assert.Equal(GpuChoice.PowerSaving, show.Get(Chrome));
            Assert.Equal(GpuChoice.PowerSaving, show.Get(Explorer));
            Assert.Equal(GpuChoice.HighPerformance, show.Get(TouchDesigner));
        }

        // With none active, applying the new profile must not put back what OmenMon manages
        [Fact]
        public void AddApps_NoneActive_MakesPowerSaving_WithTheAppsManagedNow() {
            var profiles = new SortedList<string, GraphicsProfile>(StringComparer.OrdinalIgnoreCase);
            var managed = new Dictionary<string, GraphicsManaged>(StringComparer.OrdinalIgnoreCase) {
                [NvContainer] = new GraphicsManaged(null, "GpuPreference=1;") };
            var p = GraphicsProfile.AddApps(profiles, "", new[] { Chrome, "" }, GpuChoice.PowerSaving, managed);
            Assert.Equal(GraphicsProfile.PowerSavingName, p.Name);
            Assert.Same(p, profiles[GraphicsProfile.PowerSavingName]);
            Assert.Equal(new[] { NvContainer, Chrome }, p.Apps.Select(a => a.Key));
            Assert.All(p.Apps, a => Assert.Equal(GpuChoice.PowerSaving, a.Choice));

            // And a missing active profile is the same as none
            var q = GraphicsProfile.AddApps(profiles, "Gone", new[] { Explorer }, GpuChoice.PowerSaving, managed);
            Assert.Same(p, q);
            Assert.Equal(3, q.Apps.Count);
        }
#endregion

#region Planner
        private readonly Dictionary<string, string> reg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GraphicsManaged> managed = new Dictionary<string, GraphicsManaged>(StringComparer.OrdinalIgnoreCase);

        private GraphicsPlan Plan(GraphicsProfile? target) {
            return GraphicsPlanner.Plan(target!, this.reg, this.managed);
        }

        // Carries a plan out as GraphicsApps does, on the dictionaries
        private GraphicsPlan Apply(GraphicsProfile? target) {
            GraphicsPlan plan = Plan(target);
            foreach(GraphicsAction a in plan.Actions)
                switch(a.Kind) {
                    case GraphicsActionKind.Set:
                        this.managed.TryGetValue(a.App, out GraphicsManaged? m);
                        this.managed[a.App] = new GraphicsManaged(m != null ? m.Original : a.Before, a.After);
                        Write(a.App, a.After);
                        break;
                    case GraphicsActionKind.Restore:
                        Write(a.App, a.After);
                        this.managed.Remove(a.App);
                        break;
                    case GraphicsActionKind.Unmanage:
                        this.managed.Remove(a.App);
                        break;
                }
            return plan;
        }

        private void Write(string app, string? value) {
            if(value == null)
                this.reg.Remove(app);
            else
                this.reg[app] = value;
        }

        private string? Reg(string app) {
            return this.reg.TryGetValue(app, out string? v) ? v : null;
        }

        private static GraphicsProfile Profile(string name, params (string App, GpuChoice Choice)[] apps) {
            var p = new GraphicsProfile(name);
            foreach(var a in apps)
                p.Set(a.App, a.Choice);
            return p;
        }

        [Fact]
        public void Plan_NewApp_IsSet_AndItsValueKeptAsTheOriginal() {
            this.reg[Chrome] = "GpuPreference=2;";
            GraphicsAction a = Assert.Single(Plan(Profile("P", (Chrome, GpuChoice.PowerSaving))).Actions);
            Assert.Equal(GraphicsActionKind.Set, a.Kind);
            Assert.True(a.Manage);
            Assert.True(a.Writes);
            Assert.True(a.ChoiceChanges);
            Assert.Equal("GpuPreference=2;", a.Before);
            Assert.Equal("GpuPreference=1;", a.After);
            Assert.Contains("high performance -> power saving", a.Describe());
        }

        [Fact]
        public void Plan_AppWithoutAValue_GetsOne_AndWindowsDefaultRemovesIt() {
            Apply(Profile("P", (Explorer, GpuChoice.PowerSaving)));
            Assert.Equal("GpuPreference=1;", Reg(Explorer));
            Assert.Null(this.managed[Explorer].Original);
            GraphicsAction a = Assert.Single(Apply(null).Actions);
            Assert.Equal(GraphicsActionKind.Restore, a.Kind);
            Assert.Null(a.After);
            Assert.True(a.ChoiceChanges);
            Assert.False(this.reg.ContainsKey(Explorer));
            Assert.Empty(this.managed);
        }

        [Fact]
        public void Plan_ManagedAppNotInTheProfile_GetsItsOriginalBack() {
            this.reg[Chrome] = "GpuPreference=2;";
            Apply(Profile("A", (Chrome, GpuChoice.PowerSaving), (TouchDesigner, GpuChoice.HighPerformance)));
            GraphicsPlan plan = Apply(Profile("B", (TouchDesigner, GpuChoice.HighPerformance)));
            Assert.Equal(GraphicsActionKind.NoChange, plan.Actions[0].Kind);
            Assert.Equal(GraphicsActionKind.Restore, plan.Actions[1].Kind);
            Assert.Equal("GpuPreference=2;", Reg(Chrome));
            Assert.Equal(new[] { Chrome }, plan.ChoiceChanged);
            Assert.False(this.managed.ContainsKey(Chrome));
            Assert.True(this.managed.ContainsKey(TouchDesigner));
        }

        // Windows Settings changed the app's choice: the user's choice wins
        [Fact]
        public void Plan_ChangedOutside_IsLeftAsItIs_AndNoLongerManaged() {
            this.reg[Chrome] = "GpuPreference=2;";
            GraphicsProfile p = Profile("P", (Chrome, GpuChoice.PowerSaving), (Explorer, GpuChoice.PowerSaving));
            Apply(p);
            this.reg[Chrome] = "GpuPreference=2;";      // set back in Windows Settings
            this.reg.Remove(Explorer);                  // "let Windows decide" there

            GraphicsPlan plan = Apply(p);
            Assert.All(plan.Actions, a => Assert.Equal(GraphicsActionKind.Unmanage, a.Kind));
            Assert.All(plan.Actions, a => Assert.False(a.ChoiceChanges));
            Assert.Contains("changed outside OmenMon (now high performance, OmenMon had set power saving): left as it is", plan.Actions[0].Describe());
            Assert.Equal("GpuPreference=2;", Reg(Chrome));
            Assert.Null(Reg(Explorer));
            Assert.Empty(this.managed);

            // The next explicit apply manages it again, from its new value
            plan = Apply(p);
            Assert.All(plan.Actions, a => Assert.True(a.Manage));
            Assert.Equal("GpuPreference=1;", Reg(Chrome));
            Apply(null);
            Assert.Equal("GpuPreference=2;", Reg(Chrome));
            Assert.Null(Reg(Explorer));
        }

        [Fact]
        public void Plan_ChangedOutside_NotInTheProfile_IsNotRestored() {
            this.reg[Chrome] = "GpuPreference=2;";
            Apply(Profile("P", (Chrome, GpuChoice.PowerSaving)));
            this.reg[Chrome] = "GpuPreference=0;";
            GraphicsAction a = Assert.Single(Apply(null).Actions);
            Assert.Equal(GraphicsActionKind.Unmanage, a.Kind);
            Assert.Equal("GpuPreference=0;", Reg(Chrome));
        }

        // Another pair Windows keeps in the value is not the user's GPU choice
        [Fact]
        public void Plan_OtherPairChangedInWindows_IsNotAChange_AndSurvivesTheRestore() {
            GraphicsProfile p = Profile("P", (Chrome, GpuChoice.PowerSaving));
            Apply(p);
            this.reg[Chrome] = "SwapEffectUpgradeEnable=1;GpuPreference=1;";
            Assert.Equal(GraphicsActionKind.NoChange, Assert.Single(Plan(p).Actions).Kind);
            GraphicsAction a = Assert.Single(Apply(null).Actions);
            Assert.Equal(GraphicsActionKind.Restore, a.Kind);
            Assert.Equal("SwapEffectUpgradeEnable=1;", Reg(Chrome));
        }

        [Fact]
        public void Plan_AlreadyAsTheProfileHasIt_IsManagedWithoutAWrite() {
            this.reg[TouchDesigner] = "GpuPreference=2;";
            GraphicsAction a = Assert.Single(Plan(Profile("P", (TouchDesigner, GpuChoice.HighPerformance))).Actions);
            Assert.Equal(GraphicsActionKind.Set, a.Kind);
            Assert.True(a.Manage);
            Assert.False(a.Writes);
            Assert.False(a.ChoiceChanges);
            Assert.Contains("already", a.Describe());
        }

        [Fact]
        public void Plan_LetWindowsDecide_FromNoValue_ChangesNoGpu() {
            GraphicsAction a = Assert.Single(Plan(Profile("P", (Chrome, GpuChoice.Auto))).Actions);
            Assert.True(a.Writes);
            Assert.False(a.ChoiceChanges);
            Assert.Equal("GpuPreference=0;", a.After);
        }

        [Fact]
        public void Plan_ProfileToProfile_ThenWindowsDefault_GivesTheOriginalsBack() {
            this.reg[Chrome] = "GpuPreference=2;";
            this.reg[TouchDesigner] = "SwapEffectUpgradeEnable=1;GpuPreference=2;";
            Apply(Profile("Show", (TouchDesigner, GpuChoice.HighPerformance), (Chrome, GpuChoice.PowerSaving), (Explorer, GpuChoice.PowerSaving)));
            Apply(Profile("Battery", (TouchDesigner, GpuChoice.PowerSaving), (Chrome, GpuChoice.Auto)));
            Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=1;", Reg(TouchDesigner));
            Assert.Null(Reg(Explorer));
            Apply(null);
            Assert.Equal("GpuPreference=2;", Reg(Chrome));
            Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=2;", Reg(TouchDesigner));
            Assert.Equal(2, this.reg.Count);
            Assert.Empty(this.managed);
        }

        // Values as Windows writes them, and some it might
        private static readonly string?[] Values = {
            null, "GpuPreference=0;", "GpuPreference=1;", "GpuPreference=2;", "SwapEffectUpgradeEnable=1;",
            "SwapEffectUpgradeEnable=1;GpuPreference=2;AutoHDREnable=0;", "A=1", "GpuPreference=1;B=x;", "VRROptimizeEnable=0;GpuPreference=0" };

        private static readonly string[] AppPool = { Chrome, TouchDesigner, Ableton, NvContainer, Explorer, OneDrive, Terminal };

        private static GraphicsProfile RandomProfile(Random random, string name) {
            var p = new GraphicsProfile(name);
            foreach(string app in AppPool)
                if(random.Next(3) > 0)
                    p.Set(app, (GpuChoice) random.Next(3));
            return p;
        }

        // Property: whatever profiles were applied, Windows default gives every app its
        // original back exactly, and applying a profile twice changes nothing the second time
        [Fact]
        public void Property_AnyProfiles_ThenWindowsDefault_IsExactlyTheOriginals_AndApplyTwiceChangesNothing() {
            var random = new Random(20261020);
            for(int round = 0; round < 300; round++) {
                this.reg.Clear();
                this.managed.Clear();
                foreach(string app in AppPool) {
                    string? v = Values[random.Next(Values.Length)];
                    if(v != null)
                        this.reg[app] = v;
                }
                var originals = new Dictionary<string, string>(this.reg, StringComparer.OrdinalIgnoreCase);

                int profiles = 1 + random.Next(4);
                for(int i = 0; i < profiles; i++) {
                    GraphicsProfile p = RandomProfile(random, "P" + i);
                    Apply(p);
                    Assert.False(Plan(p).HasChanges, "round " + round + ": a second apply would change something");
                    foreach(GraphicsProfileApp app in p.Apps)
                        Assert.Equal(app.Choice, GpuValue.Choice(Reg(app.Key)!));
                }
                Apply(null);
                Assert.Equal(originals.OrderBy(o => o.Key), this.reg.OrderBy(o => o.Key));
                Assert.Empty(this.managed);
                Assert.False(Plan(null).HasChanges);
            }
        }

        // Property: pairs other than GpuPreference survive every round trip, even when
        // Windows changes them in between
        [Fact]
        public void Property_OtherPairs_SurviveEveryRoundTrip() {
            var random = new Random(16);
            string[] others = { "SwapEffectUpgradeEnable=1", "AutoHDREnable=0", "VRROptimizeEnable=1", "X=y" };
            for(int round = 0; round < 300; round++) {
                this.reg.Clear();
                this.managed.Clear();
                string Pairs() {
                    var picked = others.Where(o => random.Next(2) == 0).ToList();
                    return picked.Count == 0 ? "" : string.Join(";", picked) + ";";
                }
                string mine = Pairs();
                this.reg[Chrome] = mine + "GpuPreference=" + random.Next(3) + ";";
                Apply(RandomProfile(random, "P"));
                if(random.Next(2) == 0 && Reg(Chrome) != null) {
                    // Windows rewrites the other pairs (another setting changed there)
                    mine = Pairs();
                    this.reg[Chrome] = mine + "GpuPreference=" + GpuValue.Token(Reg(Chrome)!) + ";";
                }
                Apply(RandomProfile(random, "Q"));
                Assert.Equal(GpuValue.Others(mine + "GpuPreference=0;"), GpuValue.Others(Reg(Chrome)!));
                Apply(null);
                Assert.Equal(GpuValue.Others(mine + "GpuPreference=0;"), GpuValue.Others(Reg(Chrome)!));
            }
        }
#endregion

#region Adoption
        // This laptop's entries of 2026-10-16 (four set by hand before, three new since)
        private Dictionary<string, string> LaptopNow() {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                [TouchDesigner] = "GpuPreference=2;",
                ["DirectXUserGlobalSettings"] = "SwapEffectUpgradeEnable=1;AutoHDREnable=1;VRROptimizeEnable=0;",
                [Chrome] = "GpuPreference=1;",
                [Ableton] = "GpuPreference=2;",
                [NvContainer] = "GpuPreference=1;",
                ["GraphicsFeaturesNotificationConfig"] = null!,      // a dword: not a string
                [Explorer] = "GpuPreference=1;",
                [OneDrive] = "GpuPreference=1;",
                [Terminal] = "GpuPreference=1;",
                [@"C:\Tools\NoToken.exe"] = "SwapEffectUpgradeEnable=1;",
                [@"C:\Tools\Unknown.exe"] = "GpuPreference=7;" };
        }

        private const string LaptopReg = "Windows Registry Editor Version 5.00\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\Microsoft\\DirectX\\UserGpuPreferences]\r\n"
            + "\"C:\\\\Program Files\\\\Derivative\\\\TouchDesigner\\\\bin\\\\TouchDesigner.exe\"=\"GpuPreference=2;\"\r\n"
            + "\"DirectXUserGlobalSettings\"=\"SwapEffectUpgradeEnable=1;AutoHDREnable=1;VRROptimizeEnable=0;\"\r\n"
            + "\"C:\\\\Program Files\\\\Google\\\\Chrome\\\\Application\\\\chrome.exe\"=\"GpuPreference=2;\"\r\n"
            + "\"C:\\\\ProgramData\\\\Ableton\\\\Live 12 Suite\\\\Program\\\\Ableton Live 12 Suite.exe\"=\"GpuPreference=2;\"\r\n"
            + "\"C:\\\\WINDOWS\\\\System32\\\\DriverStore\\\\FileRepository\\\\nvhmsi.inf_amd64_77e3e5801586496b\\\\Display.NvContainer\\\\NVDisplay.Container.exe\"=\"GpuPreference=1;\"\r\n"
            + "\"GraphicsFeaturesNotificationConfig\"=dword:00000001\r\n\r\n";

        private static byte[] Utf16WithBom(string text) {
            return new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray();
        }

        [Fact]
        public void Adopt_ThisLaptop_ProfileOfTheAppsWithAToken_OriginalsFromTheExport() {
            Dictionary<string, string> originals = RegFile.ReadValues(RegFile.Decode(Utf16WithBom(LaptopReg)),
                RegistryGpuPreferences.KeyPath, out string? error)!;
            Assert.Null(error);
            GraphicsProfile show = GraphicsAdoption.Build("Show", LaptopNow(), originals, this.managed);

            Assert.Equal(new[] { TouchDesigner, Chrome, Ableton, NvContainer, Explorer, OneDrive, Terminal }, show.Apps.Select(a => a.Key));
            Assert.Equal(GpuChoice.HighPerformance, show.Get(TouchDesigner));
            Assert.Equal(GpuChoice.PowerSaving, show.Get(Chrome));
            Assert.Equal(GpuChoice.PowerSaving, show.Get(Terminal));
            Assert.Equal("GpuPreference=2;", this.managed[Chrome].Original);
            Assert.Equal("GpuPreference=1;", this.managed[Chrome].Written);
            Assert.Null(this.managed[Explorer].Original);
            Assert.Null(this.managed[Terminal].Original);
            Assert.Equal(7, this.managed.Count);

            // Windows default then gives back exactly what the export holds
            foreach(KeyValuePair<string, string> v in LaptopNow())
                if(v.Value != null)
                    this.reg[v.Key] = v.Value;
            Assert.False(Plan(show).HasChanges);
            GraphicsPlan back = Apply(null);
            Assert.Equal(new[] { Chrome, Explorer, OneDrive, Terminal }, back.ChoiceChanged);
            Assert.Equal("GpuPreference=2;", Reg(Chrome));
            Assert.Equal("GpuPreference=1;", Reg(NvContainer));
            Assert.Null(Reg(Explorer));
            Assert.Null(Reg(Terminal));
            Assert.Equal("GpuPreference=7;", Reg(@"C:\Tools\Unknown.exe"));
            Assert.Equal("SwapEffectUpgradeEnable=1;AutoHDREnable=1;VRROptimizeEnable=0;", Reg("DirectXUserGlobalSettings"));
        }

        [Fact]
        public void Adopt_WithoutOriginals_WindowsDefaultChangesNothing() {
            foreach(KeyValuePair<string, string> v in LaptopNow())
                if(v.Value != null)
                    this.reg[v.Key] = v.Value;
            GraphicsAdoption.Build("Show", LaptopNow(), null!, this.managed);
            Assert.All(this.managed.Values, m => Assert.Equal(m.Written, m.Original));
            GraphicsPlan back = Apply(null);
            Assert.All(back.Actions, a => Assert.False(a.Writes));
            Assert.Empty(this.managed);
        }
#endregion

#region .reg files
        [Fact]
        public void Reg_Utf16_TheLaptopsExport() {
            Dictionary<string, string> values = RegFile.ReadValues(RegFile.Decode(Utf16WithBom(LaptopReg)),
                RegistryGpuPreferences.KeyPath, out string? error)!;
            Assert.Null(error);
            Assert.Equal(5, values.Count);
            Assert.Equal("GpuPreference=2;", values[TouchDesigner]);
            Assert.Equal("GpuPreference=1;", values[NvContainer.ToLowerInvariant()]);
            Assert.False(values.ContainsKey("GraphicsFeaturesNotificationConfig"));
        }

        [Fact]
        public void Reg_Utf8_WithAndWithoutBom_AndUtf16WithoutBom() {
            foreach(byte[] bytes in new[] {
                new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(LaptopReg)).ToArray(),
                Encoding.UTF8.GetBytes(LaptopReg),
                Encoding.Unicode.GetBytes(LaptopReg) }) {
                Dictionary<string, string>? values = RegFile.ReadValues(RegFile.Decode(bytes), RegistryGpuPreferences.KeyPath, out string? error);
                Assert.Null(error);
                Assert.Equal("GpuPreference=2;", values![Chrome]);
            }
        }

        [Fact]
        public void Reg_EscapesOtherKeysHexAndDefaultValues() {
            string text = "REGEDIT4\n\n"
                + "[HKEY_CURRENT_USER\\Software\\Other]\n\"a.exe\"=\"GpuPreference=2;\"\n\n"
                + "[HKEY_USERS\\S-1-5-21-1-2-3-1001\\Software\\Microsoft\\DirectX\\UserGpuPreferences]\n"
                + "@=\"default value\"\n"
                + "\"Blob\"=hex:01,02,\\\n  03,04,\\\n  05\n"
                + "; a comment\n"
                + "\"C:\\\\Quote\\\"d\\\\app.exe\"=\"GpuPreference=1;Note=\\\"x\\\";\"\n"
                + "\"Gone\"=-\n";
            Dictionary<string, string>? values = RegFile.ReadValues(text, RegistryGpuPreferences.KeyPath, out string? error);
            Assert.Null(error);
            Assert.Equal("GpuPreference=1;Note=\"x\";", Assert.Single(values!).Value);
            Assert.Equal("C:\\Quote\"d\\app.exe", values!.Keys.Single());
        }

        [Theory]
        [InlineData("just text\r\n[HKEY_CURRENT_USER\\Software\\Microsoft\\DirectX\\UserGpuPreferences]\r\n", "not a .reg export")]
        [InlineData("Windows Registry Editor Version 5.00\r\n[HKEY_CURRENT_USER\\Software\\Other]\r\n", "no [HKEY_CURRENT_USER")]
        [InlineData("Windows Registry Editor Version 5.00\r\n[HKEY_CURRENT_USER\\Software\\Microsoft\\DirectX\\UserGpuPreferences]\r\n\"a.exe\"=\"x\r\n", "line 3")]
        [InlineData("Windows Registry Editor Version 5.00\r\n[HKEY_CURRENT_USER\\Software\\Microsoft\\DirectX\\UserGpuPreferences]\r\n\"a.exe\"=\"x\" junk\r\n", "line 3")]
        public void Reg_Unreadable_SaysWhy(string text, string why) {
            Assert.Null(RegFile.ReadValues(text, RegistryGpuPreferences.KeyPath, out string? error));
            Assert.Contains(why, error);
        }
#endregion

    }

}
