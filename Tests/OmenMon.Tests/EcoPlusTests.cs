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

        // The internal panel ("panel") and any other displays; a display not in Rates is not active
        private sealed class FakeDisplay : IDisplayRate {
            public readonly Dictionary<string, int> Rates = new Dictionary<string, int> { ["panel"] = 165 };
            public string? PanelId = "panel";
            public int Sets;
            public bool Fail;
            public int Hz { get { return this.Rates["panel"]; } set { this.Rates["panel"] = value; } }
            public string Panel() { return this.PanelId != null && this.Rates.ContainsKey(this.PanelId) ? this.PanelId : null!; }
            public int Current(string d) { return d != null && this.Rates.TryGetValue(d, out int v) ? v : 0; }
            public bool SetDynamic(string d, int hz) {
                if(this.Fail || d == null || !this.Rates.ContainsKey(d)) return false;
                if(this.Rates[d] != hz) this.Sets++;
                this.Rates[d] = hz;
                return true;
            }
        }

        private static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        private static readonly Guid HighPerf = new Guid("9157a280-24ee-42f4-935f-207d41dcfd34");

        private readonly string dir = Path.Combine(Path.GetTempPath(), "omenmon-ecoplus-" + Guid.NewGuid().ToString("N"));
        private readonly FakePlan plan = new FakePlan();
        private readonly FakeDisplay display = new FakeDisplay();
        private EcoPlusOptions options = new EcoPlusOptions { Enabled = true, RefreshHoldSeconds = 0 };
        private DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        public EcoPlusTests() {
            Directory.CreateDirectory(this.dir);
        }

        public void Dispose() {
            try { Directory.Delete(this.dir, true); } catch { }
        }

        private string StatePath { get { return Path.Combine(this.dir, EcoPlusState.FileName); } }

        private EcoPlus NewEco() {
            return new EcoPlus(this.plan, this.display, EcoPlusState.Load(StatePath), () => this.options, () => this.now);
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
            this.options = new EcoPlusOptions { Enabled = true, RefreshRateOnBattery = 60, RefreshHoldSeconds = 0 };
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
            this.options = new EcoPlusOptions { Enabled = true, RefreshRateOnBattery = 60, RefreshHoldSeconds = 0 };
            var eco = NewEco();
            eco.Maintain(true);
            this.options = new EcoPlusOptions { Enabled = true, RefreshRateOnBattery = 0, RefreshHoldSeconds = 0 };
            eco.Maintain(true);
            Assert.Equal(165, this.display.Hz);
        }

        // A flaky socket (seen on a train: drops of 5 s to 2 min, 18 a day): each refresh
        // change blanks the panel, so nothing changes until the power source is stable
        private EcoPlus HeldEco() {
            this.options = new EcoPlusOptions { Enabled = true, RefreshRateOnBattery = 60, RefreshHoldSeconds = 120 };
            var eco = NewEco();
            eco.Maintain(false);                // plugged in for a long time
            this.now = this.now.AddHours(1);
            return eco;
        }

        private void Tick(EcoPlus eco, bool onBattery, int seconds) {
            for(int i = 0; i < seconds; i++) {
                eco.Maintain(onBattery);
                this.now = this.now.AddSeconds(1);
            }
        }

        [Fact]
        public void Refresh_ShortDrops_NeverChangeTheRate() {
            var eco = HeldEco();
            for(int drop = 0; drop < 18; drop++) {
                Tick(eco, true, 5 + drop * 6);     // 5 .. 107 s on battery
                Tick(eco, false, 30);
            }
            Assert.Equal(0, this.display.Sets);
            Assert.Equal(165, this.display.Hz);
            Assert.Null(eco.State.RefreshBefore);
        }

        [Fact]
        public void Refresh_StableOnBattery_LowersAfterTheHold() {
            var eco = HeldEco();
            Tick(eco, true, 120);                // t = 0 .. 119 s
            Assert.Equal(165, this.display.Hz);
            Assert.True(eco.RefreshPending);
            eco.Maintain(true);                  // t = 120 s
            Assert.Equal(60, this.display.Hz);
            Assert.False(eco.RefreshPending);
            Assert.Equal(1, this.display.Sets);
        }

        [Fact]
        public void Refresh_BackOnAc_RestoresOnlyWhenStable() {
            var eco = HeldEco();
            Tick(eco, true, 121);
            Assert.Equal(60, this.display.Hz);
            Tick(eco, false, 10);                // power comes back briefly
            Tick(eco, true, 30);                 // and drops again
            Assert.Equal(60, this.display.Hz);
            Assert.Equal(1, this.display.Sets);
            Tick(eco, false, 120);
            Assert.True(eco.RefreshPending);
            eco.Maintain(false);
            Assert.Equal(165, this.display.Hz);
            Assert.Null(eco.State.RefreshBefore);
            Assert.Equal(2, this.display.Sets);
        }

        [Fact]
        public void Refresh_TurningOff_RestoresAtOnce_EvenWhileUnstable() {
            var eco = HeldEco();
            Tick(eco, true, 121);
            Tick(eco, false, 3);
            this.options = new EcoPlusOptions { Enabled = true, RefreshRateOnBattery = 0, RefreshHoldSeconds = 120 };
            eco.Maintain(false);
            Assert.Equal(165, this.display.Hz);

            Tick(eco, true, 121);
            this.options = new EcoPlusOptions { Enabled = true, RefreshRateOnBattery = 60, RefreshHoldSeconds = 120 };
            Tick(eco, true, 1);                  // on battery for long: lowers at once
            Assert.Equal(60, this.display.Hz);
            Tick(eco, false, 3);
            this.options = new EcoPlusOptions { Enabled = false };
            eco.Maintain(false);
            Assert.Equal(165, this.display.Hz);
        }

        // Once per battery period: a rate someone else set is theirs
        [Fact]
        public void Refresh_RaisedByTheUserOnBattery_IsLeftAlone_UntilThePowerSourceChanges() {
            var eco = HeldEco();
            Tick(eco, true, 121);
            Assert.Equal(60, this.display.Hz);
            this.display.Hz = 165;               // tray: 165 Hz to watch a video
            Tick(eco, true, 600);
            Assert.Equal(165, this.display.Hz);
            Assert.Equal(1, this.display.Sets);
            Assert.Null(eco.State.RefreshBefore);
            Tick(eco, false, 300);               // plugged in: nothing to put back
            Assert.Equal(165, this.display.Hz);
            Tick(eco, true, 121);                // the next battery period lowers again
            Assert.Equal(60, this.display.Hz);
            Assert.Equal(2, this.display.Sets);
        }

        [Fact]
        public void Refresh_PresetOnBattery_IsNotOverriddenOnAc() {
            var eco = HeldEco();
            Tick(eco, true, 121);
            this.display.Hz = 48;                // a preset or Windows Settings
            Tick(eco, true, 60);
            Tick(eco, false, 300);
            Assert.Equal(48, this.display.Hz);
            Assert.Null(eco.State.RefreshBefore);
        }

        [Fact]
        public void Refresh_SetByTheUser_EvenToTheSameRate_IsNotPutBack() {
            var eco = HeldEco();
            Tick(eco, true, 121);
            eco.NoteRefreshSetByUser();          // the Eco preset: 60 Hz saved as the mode
            Assert.Null(eco.State.RefreshBefore);
            Tick(eco, false, 300);
            Assert.Equal(60, this.display.Hz);
            Assert.Contains("set by the user", LogText);
        }

        [Fact]
        public void Refresh_ChangedWhileNotRunning_IsNotRestored() {
            var eco = HeldEco();
            Tick(eco, true, 121);
            var again = NewEco();                // OmenMon restarted; meanwhile the user set 144 Hz
            this.display.Hz = 144;
            string done = null!;
            for(int i = 0; i < 130; i++) { done = again.Maintain(false) ?? done; this.now = this.now.AddSeconds(1); }
            Assert.Equal(144, this.display.Hz);
            Assert.Null(again.State.RefreshBefore);
            Assert.Contains("left at 144 Hz", done);
        }

        [Fact]
        public void Refresh_WindowsResetOnResume_IsLoweredAgainOnce() {
            var eco = HeldEco();
            Tick(eco, true, 121);
            this.display.Hz = 165;               // resume from sleep: Windows put the saved mode back
            eco.NoteDisplayReset();
            Tick(eco, true, 5);
            Assert.Equal(60, this.display.Hz);
            Assert.Equal(2, this.display.Sets);
            this.display.Hz = 165;               // again in the same period: left alone
            eco.NoteDisplayReset();
            Tick(eco, true, 60);
            Assert.Equal(165, this.display.Hz);
            Assert.Null(eco.State.RefreshBefore);
        }

        [Fact]
        public void Refresh_AfterARebootOnBattery_IsLoweredAgain_AndRestoredOnAc() {
            var eco = HeldEco();
            Tick(eco, true, 121);
            this.display.Hz = 165;               // rebooted: the saved mode is back
            var again = NewEco();
            Tick(again, true, 121);
            Assert.Equal(60, this.display.Hz);
            Tick(again, false, 121);
            Assert.Equal(165, this.display.Hz);
            Assert.Null(again.State.RefreshBefore);
        }

        [Fact]
        public void Refresh_OnlyThePanelIsChanged_NotAnotherDisplay() {
            this.display.Rates["wall"] = 144;    // LED wall output
            var eco = HeldEco();
            Tick(eco, true, 121);
            Assert.Equal(60, this.display.Hz);
            Assert.Equal(144, this.display.Rates["wall"]);
            Assert.Equal("panel", eco.State.RefreshDisplay);
            Assert.Equal(60, eco.State.RefreshSet);
        }

        [Fact]
        public void Refresh_PanelGone_IsNotRestoredOnAnotherDisplay() {
            var eco = HeldEco();
            Tick(eco, true, 121);
            this.display.Rates.Remove("panel");  // lid closed, only the external display left
            this.display.Rates["wall"] = 60;
            string done = null!;
            for(int i = 0; i < 130; i++) { done = eco.Maintain(false) ?? done; this.now = this.now.AddSeconds(1); }
            Assert.Equal(60, this.display.Rates["wall"]);
            Assert.Null(eco.State.RefreshBefore);
            Assert.Contains("not active", done);
        }

        [Fact]
        public void Refresh_NoPanel_NothingIsLowered() {
            this.display.PanelId = null;
            this.display.Rates["wall"] = 144;
            var eco = HeldEco();
            Tick(eco, true, 200);
            Assert.Equal(144, this.display.Rates["wall"]);
            Assert.Equal(0, this.display.Sets);
        }

        [Fact]
        public void Refresh_FailedRestore_IsRetried_ThenDropped() {
            var eco = HeldEco();
            Tick(eco, true, 121);
            this.display.Fail = true;
            Tick(eco, false, 121);
            Assert.NotNull(eco.State.RefreshBefore);
            Tick(eco, false, 3);
            Assert.Null(eco.State.RefreshBefore);
        }

        [Fact]
        public void Refresh_OldStateLine_IsRestoredWhileStillLower() {
            File.WriteAllText(StatePath, "refresh\t165\r\n");
            this.display.Hz = 60;
            var eco = NewEco();
            Assert.Null(eco.State.RefreshDisplay);
            this.options = new EcoPlusOptions { Enabled = false };
            eco.Maintain(false);
            Assert.Equal(165, this.display.Hz);

            File.WriteAllText(StatePath, "refresh\t165\r\n");
            NewEco().Maintain(false);            // already back at 165: nothing to do
            Assert.Equal(1, this.display.Sets);
            Assert.False(File.Exists(StatePath));
        }

        [Fact]
        public void State_KeepsTheRateSetAndTheDisplay() {
            var s = new EcoPlusState(StatePath) { RefreshBefore = 165, RefreshSet = 60, RefreshDisplay = @"\\?\DISPLAY#BOE0B7C#4&1&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}" };
            Assert.True(s.Save());
            var t = EcoPlusState.Load(StatePath);
            Assert.Equal(165, t.RefreshBefore);
            Assert.Equal(60, t.RefreshSet);
            Assert.Equal(s.RefreshDisplay, t.RefreshDisplay);
        }

        [Fact]
        public void Boost_IsNotHeld() {
            var eco = HeldEco();
            eco.Maintain(true);
            Assert.Equal(0u, this.plan.Dc[Balanced]);
        }

        [Fact]
        public void Log_AppendsAndStaysSmall() {
            string log = Path.Combine(this.dir, EcoPlusLog.FileName);
            for(int i = 0; i < 3000; i++)
                EcoPlusLog.Append(log, this.now, "on battery: refresh 165 -> 60 Hz #" + i);
            Assert.True(new FileInfo(log).Length <= EcoPlusLog.MaxBytes + 200);
            string[] lines = File.ReadAllLines(log);
            Assert.EndsWith("#2999", lines[lines.Length - 1]);
            Assert.StartsWith("2026-10-16 12:00:00	", lines[0]);
        }

        [Fact]
        public void Boost_ChangedSince_IsNotRestored() {
            var eco = NewEco();
            eco.Maintain(true);
            this.plan.Dc[Balanced] = 3;          // powercfg while Eco+ was on
            string done = eco.RestoreSettings();
            Assert.Equal(3u, this.plan.Dc[Balanced]);
            Assert.Contains("left at efficient enabled", done);
            Assert.True(eco.State.IsEmpty);
        }

        // On exit the battery settings go back, and nothing changes afterwards
        [Fact]
        public void Exit_RestoresBoostAndRefresh_AndChangesNothingAfterwards() {
            this.options = new EcoPlusOptions { Enabled = true, RefreshRateOnBattery = 60, RefreshHoldSeconds = 0 };
            var eco = NewEco();
            eco.Maintain(true);
            Assert.NotNull(eco.RestoreForExit());
            Assert.Equal(2u, this.plan.Dc[Balanced]);
            Assert.Equal(165, this.display.Hz);
            Assert.Null(eco.Maintain(true));
            Assert.Equal(2u, this.plan.Dc[Balanced]);
            Assert.Equal(165, this.display.Hz);
            Assert.False(File.Exists(StatePath));
        }

        [Fact]
        public void RestoreAll_PutsEverythingBack_AsTheCliDoes() {
            this.options = new EcoPlusOptions { Enabled = true, RefreshRateOnBattery = 60, RefreshHoldSeconds = 0 };
            NewEco().Maintain(true);
            this.options = new EcoPlusOptions { Enabled = false };
            var cli = NewEco();                  // a separate process, from the state file alone
            string done = cli.RestoreAll();
            Assert.Contains("boost on battery back to 2", done);
            Assert.Contains("refresh back to 165 Hz", done);
            Assert.Equal(2u, this.plan.Dc[Balanced]);
            Assert.Equal(165, this.display.Hz);
            Assert.False(File.Exists(StatePath));
        }

        // GPU preferences are Graphics apps' now: the lines an earlier Eco+ saved are kept
        // (for GraphicsMigration) through every save, and RestoreAll leaves them alone
        [Fact]
        public void OldGpuLines_AreKeptThroughSaves_AndNotRestored() {
            File.WriteAllText(StatePath, "gpu\ta.exe\tGpuPreference=2;\r\ngpu\tb.exe\t\r\n");
            this.options = new EcoPlusOptions { Enabled = true, RefreshRateOnBattery = 60, RefreshHoldSeconds = 0 };
            var eco = NewEco();
            eco.Maintain(true);                  // saves boost and refresh
            var again = EcoPlusState.Load(StatePath);
            Assert.Equal(2u, again.SavedBoostDc[Balanced]);
            Assert.Equal(2, again.SavedGpu.Count);
            Assert.Equal("GpuPreference=2;", again.SavedGpu["a.exe"]);
            Assert.Null(again.SavedGpu["b.exe"]);
            this.options = new EcoPlusOptions { Enabled = false };
            string done = NewEco().RestoreAll();
            Assert.DoesNotContain("GPU", done);
            Assert.Equal(2, EcoPlusState.Load(StatePath).SavedGpu.Count);
        }

        [Fact]
        public void RestoreAll_WithNoState_DoesNothing() {
            this.plan.Dc[Balanced] = 0;
            this.display.Hz = 60;
            Assert.Null(NewEco().RestoreAll());
            Assert.Equal(0, this.plan.Writes);
            Assert.Equal(0, this.display.Sets);
            Assert.False(File.Exists(StatePath));
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

        // The state file is written before Windows is changed: no saved original, no change
        private string LogText { get { string p = Path.Combine(this.dir, EcoPlusLog.FileName); return File.Exists(p) ? File.ReadAllText(p) : ""; } }

        private void BreakSaving() { Directory.CreateDirectory(StatePath + ".tmp"); }
        private void FixSaving() { Directory.Delete(StatePath + ".tmp"); }

        [Fact]
        public void FailedSave_LeavesBoostAlone_AndIsLoggedOnce() {
            var eco = NewEco();
            BreakSaving();
            eco.Maintain(true);
            eco.Maintain(true);
            Assert.Equal(0, this.plan.Writes);
            Assert.Equal(2u, this.plan.Dc[Balanced]);
            Assert.True(eco.State.IsEmpty);
            Assert.NotNull(eco.LastError);
            Assert.Single(LogText.Split('\n'), l => l.Contains("could not save"));
            FixSaving();
            eco.Maintain(true);
            Assert.Equal(0u, this.plan.Dc[Balanced]);
            Assert.Equal(2u, EcoPlusState.Load(StatePath).SavedBoostDc[Balanced]);
            Assert.Null(eco.LastError);
        }

        [Fact]
        public void FailedSave_LeavesTheRefreshRateAlone() {
            this.plan.Dc[Balanced] = 0;
            this.options = new EcoPlusOptions { Enabled = true, RefreshRateOnBattery = 60, RefreshHoldSeconds = 0 };
            var eco = NewEco();
            BreakSaving();
            eco.Maintain(true);
            Assert.Equal(165, this.display.Hz);
            Assert.Null(eco.State.RefreshBefore);
        }

        [Fact]
        public void UnreadableState_IsKeptAsBad_AndLogged() {
            File.WriteAllText(StatePath, "boost\t{381b4222-f694-41f0-9685-ff5bb260df2e}\t2\r\n");
            EcoPlusState s;
            // Readable by no one, but it can be renamed
            using(new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Delete))
                s = EcoPlusState.Load(StatePath);
            Assert.True(s.IsEmpty);
            Assert.False(s.Blocked);
            Assert.False(File.Exists(StatePath));
            Assert.Contains("{381b4222", File.ReadAllText(StatePath + ".bad"));
            new EcoPlus(this.plan, this.display, s, () => this.options, () => this.now);
            Assert.Contains(".bad", LogText);
        }

        [Fact]
        public void UnreadableState_ThatCannotBeMoved_BlocksChanges_UntilItCanBeRead() {
            File.WriteAllText(StatePath, "boost\t{381b4222-f694-41f0-9685-ff5bb260df2e}\t2\r\n");
            this.plan.Dc[Balanced] = 0;          // as Eco+ left it before the restart
            this.plan.Dc[HighPerf] = 1;
            this.plan.Active = HighPerf;
            EcoPlus eco;
            using(new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None)) {
                eco = NewEco();
                Assert.True(eco.State.Blocked);
                eco.Maintain(true);
                Assert.Equal(0, this.plan.Writes);
                Assert.Equal(1u, this.plan.Dc[HighPerf]);
            }
            Assert.Contains("changes nothing", LogText);
            this.options = new EcoPlusOptions { Enabled = false };
            eco.Maintain(true);                  // readable again: the original is restored
            Assert.Equal(2u, this.plan.Dc[Balanced]);
            Assert.False(File.Exists(StatePath));
        }

        [Fact]
        public void InterruptedSave_IsRecoveredFromTheTemporaryFile() {
            File.WriteAllText(StatePath + ".tmp", "boost\t{381b4222-f694-41f0-9685-ff5bb260df2e}\t2\r\n");
            var eco = NewEco();
            Assert.Equal(2u, eco.State.SavedBoostDc[Balanced]);
            this.plan.Dc[Balanced] = 0;
            this.options = new EcoPlusOptions { Enabled = false };
            eco.Maintain(false);
            Assert.Equal(2u, this.plan.Dc[Balanced]);
            Assert.False(File.Exists(StatePath + ".tmp"));
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

        // The Power window must not keep the discrete GPU awake by reading NVML every 3 s
        [Fact]
        public void Nvml_OnlyWhileOn_AndBackingOffWhileIdle() {
            var n = new NvmlSchedule();
            DateTime t = new DateTime(2026, 10, 20, 12, 0, 0);
            Assert.False(n.Due(t, 4));                       // asleep: never
            Assert.True(n.Due(t, 1));                        // woke up: read once
            n.Done(t, 2);
            Assert.False(n.Due(t.AddSeconds(3), 1));
            Assert.False(n.Due(t.AddSeconds(12), 1));
            Assert.True(n.Due(t.AddSeconds(15), 1));         // apps hold it: every 15 s
            n.Done(t.AddSeconds(15), 0);
            Assert.False(n.Due(t.AddSeconds(30), 1));        // no app: every 60 s
            Assert.False(n.Due(t.AddSeconds(72), 1));
            Assert.True(n.Due(t.AddSeconds(75), 1));
            n.Done(t.AddSeconds(75), 0);
            Assert.False(n.Due(t.AddSeconds(78), 4));        // powered off
            Assert.False(n.Due(t.AddSeconds(81), 1));        // woke again within 15 s: not yet
            Assert.True(n.Due(t.AddSeconds(90), 1));         // woke, 15 s after the last reading
        }

        [Fact]
        public void Format_OldGpuDetails_ShowTheirAge() {
            DateTime t = new DateTime(2026, 10, 20, 12, 0, 0);
            var s = new PowerSnapshot { Time = t, OnAc = true, GpuPowerState = 1, GpuPowerMW = 4380, GpuDetailsTime = t.AddSeconds(-42) };
            Assert.Contains("(read 42 s ago)", PowerMath.Format(s));
            s.GpuDetailsTime = t;
            Assert.DoesNotContain("ago", PowerMath.Format(s));
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
