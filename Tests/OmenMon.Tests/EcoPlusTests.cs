  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Library;

namespace OmenMon.Tests {

    // Eco+: battery-only settings, each one restored exactly as it was
    public class EcoPlusTests : IDisposable {

        private sealed class FakePlan : IPowerPlan {
            public Guid Active = Balanced;
            public readonly Dictionary<Guid, uint> Dc = new Dictionary<Guid, uint> { [Balanced] = 2, [HighPerf] = 2 };
            public int Writes;
            public Guid ActiveScheme() { return this.Active; }
            public uint? ReadBoostDc(Guid s) { return this.Dc.TryGetValue(s, out uint v) ? v : (uint?) null; }
            public void WriteBoostDc(Guid s, uint v) { this.Dc[s] = v; this.Writes++; }
        }

        private sealed class FakeGpu : IGpuPreferences {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Get(string app) { return this.Values.TryGetValue(app, out string? v) ? v! : null!; }
            public void Set(string app, string value) { this.Values[app] = value; }
            public void Remove(string app) { this.Values.Remove(app); }
        }

        private sealed class FakeDisplay : IDisplayRate {
            public int Hz = 165;
            public int Current() { return this.Hz; }
            public bool SetDynamic(int hz) { this.Hz = hz; return true; }
        }

        private static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        private static readonly Guid HighPerf = new Guid("9157a280-24ee-42f4-935f-207d41dcfd34");

        private readonly string dir = Path.Combine(Path.GetTempPath(), "omenmon-ecoplus-" + Guid.NewGuid().ToString("N"));
        private readonly FakePlan plan = new FakePlan();
        private readonly FakeGpu gpu = new FakeGpu();
        private readonly FakeDisplay display = new FakeDisplay();
        private EcoPlusOptions options = new EcoPlusOptions { Enabled = true };

        public EcoPlusTests() {
            Directory.CreateDirectory(this.dir);
        }

        public void Dispose() {
            try { Directory.Delete(this.dir, true); } catch { }
        }

        private string StatePath { get { return Path.Combine(this.dir, EcoPlusState.FileName); } }

        private EcoPlus NewEco() {
            return new EcoPlus(this.plan, this.gpu, this.display, EcoPlusState.Load(StatePath), () => this.options);
        }

        [Fact]
        public void Enabled_TurnsBoostOffOnBattery_ForTheActiveScheme_Once() {
            var eco = NewEco();
            eco.Maintain(onBattery: false);
            eco.Maintain(onBattery: true);
            Assert.Equal(0u, this.plan.Dc[Balanced]);
            Assert.Equal(2u, this.plan.Dc[HighPerf]);
            Assert.Equal(1, this.plan.Writes);
            Assert.Equal(2u, eco.State.SavedBoostDc[Balanced]);
        }

        [Fact]
        public void Disabled_RestoresTheOriginalValue_AndRemovesTheStateFile() {
            var eco = NewEco();
            eco.Maintain(false);
            Assert.True(File.Exists(StatePath));
            this.options = new EcoPlusOptions { Enabled = false };
            eco.Maintain(false);
            Assert.Equal(2u, this.plan.Dc[Balanced]);
            Assert.True(eco.State.IsEmpty);
            Assert.False(File.Exists(StatePath));
        }

        [Fact]
        public void SchemeSwitch_IsCovered_AndBothAreRestored() {
            var eco = NewEco();
            eco.Maintain(true);
            this.plan.Active = HighPerf;
            eco.Maintain(true);
            Assert.Equal(0u, this.plan.Dc[HighPerf]);
            Assert.Equal(2, eco.RestoreSettings().Split(',').Length);
            Assert.Equal(2u, this.plan.Dc[Balanced]);
            Assert.Equal(2u, this.plan.Dc[HighPerf]);
        }

        [Fact]
        public void AlreadyDisabledBoost_IsNotRecorded_SoRestoreLeavesItAlone() {
            this.plan.Dc[Balanced] = 0;
            var eco = NewEco();
            eco.Maintain(true);
            Assert.Equal(0, this.plan.Writes);
            Assert.True(eco.State.IsEmpty);
        }

        [Fact]
        public void TheOriginal_SurvivesARestart() {
            NewEco().Maintain(true);
            var again = NewEco();               // as after an OmenMon restart
            Assert.Equal(2u, again.State.SavedBoostDc[Balanced]);
            this.options = new EcoPlusOptions { Enabled = false };
            again.Maintain(false);
            Assert.Equal(2u, this.plan.Dc[Balanced]);
        }

        [Fact]
        public void Refresh_LowOnBattery_BackOnAc() {
            this.options = new EcoPlusOptions { Enabled = true, RefreshRateOnBattery = 60 };
            var eco = NewEco();
            eco.Maintain(true);
            Assert.Equal(60, this.display.Hz);
            Assert.Equal(165, eco.State.RefreshBefore);
            eco.Maintain(false);
            Assert.Equal(165, this.display.Hz);
            Assert.Null(eco.State.RefreshBefore);
        }

        [Fact]
        public void Refresh_Zero_LeavesTheDisplayAlone() {
            var eco = NewEco();
            eco.Maintain(true);
            Assert.Equal(165, this.display.Hz);
        }

        [Fact]
        public void Refresh_OptionTurnedOffOnBattery_PutsTheRateBack() {
            this.options = new EcoPlusOptions { Enabled = true, RefreshRateOnBattery = 60 };
            var eco = NewEco();
            eco.Maintain(true);
            this.options = new EcoPlusOptions { Enabled = true, RefreshRateOnBattery = 0 };
            eco.Maintain(true);
            Assert.Equal(165, this.display.Hz);
        }

        [Fact]
        public void Gpu_Release_KeepsTheOriginals_AndRestoreMatchesThem() {
            this.gpu.Values[@"C:\Program Files\Google\Chrome\Application\chrome.exe"] = "GpuPreference=2;";
            var eco = NewEco();
            int n = eco.ReleaseGpu(new[] {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
                "microsoft.windowsterminal_8wekyb3d8bbwe!app" });   // duplicate, other case
            Assert.Equal(2, n);
            Assert.Equal(EcoPlus.GpuPowerSaving, this.gpu.Get(@"C:\Program Files\Google\Chrome\Application\chrome.exe"));
            Assert.Equal(EcoPlus.GpuPowerSaving, this.gpu.Get("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"));

            // A second release does not overwrite the saved originals
            eco.ReleaseGpu(new[] { @"C:\Program Files\Google\Chrome\Application\chrome.exe" });

            var again = NewEco();               // originals survive a restart
            Assert.Equal(2, again.RestoreGpu());
            Assert.Equal("GpuPreference=2;", this.gpu.Get(@"C:\Program Files\Google\Chrome\Application\chrome.exe"));
            Assert.Null(this.gpu.Get("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"));
            Assert.False(File.Exists(StatePath));
        }

        [Fact]
        public void Gpu_AlreadyPowerSaving_IsSkipped() {
            this.gpu.Values["a.exe"] = EcoPlus.GpuPowerSaving;
            Assert.Equal(0, NewEco().ReleaseGpu(new[] { "a.exe" }));
        }

        [Fact]
        public void State_IgnoresGarbageLines() {
            File.WriteAllText(StatePath, "boost\tnot-a-guid\t2\r\nrefresh\t-5\r\nnonsense\r\ngpu\t\tx\r\nboost\t{381b4222-f694-41f0-9685-ff5bb260df2e}\t3\r\n");
            var s = EcoPlusState.Load(StatePath);
            Assert.Single(s.SavedBoostDc);
            Assert.Equal(3u, s.SavedBoostDc[Balanced]);
            Assert.Null(s.RefreshBefore);
            Assert.Empty(s.SavedGpu);
        }

    }

    public class PowerMathTests {

        [Fact]
        public void Runtime_FromRemainingAndDraw() {
            Assert.Equal(TimeSpan.FromHours(1.5), PowerMath.Runtime(60000, 40000));
            Assert.Null(PowerMath.Runtime(60000, 0));
            Assert.Null(PowerMath.Runtime(null, 40000));
        }

        [Fact]
        public void GaugePinned_OnlyOnBattery_AtFull_WhileDischarging() {
            // Seen on 8BAC: 63980 of 63980 mWh for 3 min at 50+ W after unplugging
            var s = new PowerSnapshot { OnAc = false, RemainingMWh = 63980, FullMWh = 63980, DischargeMW = 52000 };
            Assert.True(PowerMath.IsGaugePinned(s));
            s.RemainingMWh = 60310;
            Assert.False(PowerMath.IsGaugePinned(s));
            s = new PowerSnapshot { OnAc = true, RemainingMWh = 63980, FullMWh = 63980, DischargeMW = 0 };
            Assert.False(PowerMath.IsGaugePinned(s));
        }

        [Fact]
        public void Format_OnBattery_ShowsDrawTimeAndBoost() {
            var s = new PowerSnapshot {
                OnAc = false, RemainingMWh = 33000, FullMWh = 63980, DesignMWh = 70070, DischargeMW = 39200,
                CpuLoad = 15, CpuPerformance = 81, SchemeName = "Balanced", BoostAc = 2, BoostDc = 0,
                RefreshHz = 60, GpuName = "NVIDIA GeForce RTX 4080 Laptop GPU", GpuPowerState = 1, GpuPowerMW = 4380, GpuPState = 8 };
            s.GpuHolders.Add(new GpuHolder { Pid = 12756, Name = "explorer", AppKey = @"C:\Windows\explorer.exe" });
            string t = PowerMath.Format(s);
            Assert.Contains("on battery", t);
            Assert.Contains("39.2 W", t);
            Assert.Contains("0 h 50 min", t);
            Assert.Contains("plugged in: aggressive, on battery: disabled", t);
            Assert.Contains("D0 (on)", t);
            Assert.Contains("explorer (12756)  preference: default", t);
            Assert.DoesNotContain("boosting", t);
        }

        [Fact]
        public void Format_GpuAsleep_SaysSo() {
            string t = PowerMath.Format(new PowerSnapshot { OnAc = true, GpuPowerState = 4 });
            Assert.Contains("D3 (off)", t);
            Assert.Contains("drawing no power", t);
        }

        [Theory]
        [InlineData(@"C:\Windows\System32\dwm.exe", true)]
        [InlineData(@"c:\windows\system32\csrss.exe", true)]
        [InlineData(@"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe", true)]
        [InlineData("", true)]
        [InlineData(@"C:\Windows\explorer.exe", false)]
        [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
        [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.24.12741.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe", false)]
        public void SystemComponents_AreNotOffered(string path, bool system) {
            Assert.Equal(system, AppIdentity.IsSystemComponent(path));
        }

        [Theory]
        [InlineData(null, "default")]
        [InlineData("GpuPreference=1;", "power saving")]
        [InlineData("GpuPreference=2;", "high performance")]
        [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=2;", "high performance")]
        public void Preference_Describe(string? value, string expected) {
            Assert.Equal(expected, RegistryGpuPreferences.Describe(value!));
        }

    }

}
