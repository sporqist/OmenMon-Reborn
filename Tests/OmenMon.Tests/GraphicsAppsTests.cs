  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Library;

namespace OmenMon.Tests {

    // Graphics apps: applying profiles to the GPU preferences (a fake store, never the
    // real HKCU key), the originals in OmenMon-Graphics.state, and the move of the GPU
    // preferences an earlier Eco+ kept
    public class GraphicsAppsTests : IDisposable {

        private sealed class FakeGpu : IGpuPreferences {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public int Writes;
            public string? FailOn;                  // Set and Remove of this app throw
            public Action<string>? BeforeWrite;     // runs before each write, with the app
            public IList<string> Apps() { return this.Values.Keys.ToList(); }
            public string Get(string app) { return this.Values.TryGetValue(app, out string? v) ? v : null!; }
            public void Set(string app, string value) { Write(app); this.Values[app] = value; }
            public void Remove(string app) { Write(app); this.Values.Remove(app); }
            private void Write(string app) {
                this.BeforeWrite?.Invoke(app);
                if(string.Equals(app, this.FailOn, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("Access is denied");
                this.Writes++;
            }
        }

        private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        private const string TouchDesigner = @"C:\Program Files\Derivative\TouchDesigner\bin\TouchDesigner.exe";
        private const string Explorer = @"C:\WINDOWS\explorer.exe";
        private const string Terminal = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";

        private readonly string dir = Path.Combine(Path.GetTempPath(), "omenmon-graphics-" + Guid.NewGuid().ToString("N"));
        private readonly FakeGpu gpu = new FakeGpu();

        public GraphicsAppsTests() {
            Directory.CreateDirectory(this.dir);
        }

        public void Dispose() {
            try { Directory.Delete(this.dir, true); } catch { }
        }

        private string StatePath { get { return Path.Combine(this.dir, GraphicsState.FileName); } }
        private string EcoStatePath { get { return Path.Combine(this.dir, EcoPlusState.FileName); } }
        private string LogText { get { string p = Path.Combine(this.dir, GraphicsState.LogName); return File.Exists(p) ? File.ReadAllText(p) : ""; } }

        private GraphicsApps NewApps() {
            return new GraphicsApps(this.gpu, GraphicsState.Load(StatePath));
        }

        private static GraphicsProfile Profile(string name, params (string App, GpuChoice Choice)[] apps) {
            var p = new GraphicsProfile(name);
            foreach(var a in apps)
                p.Set(a.App, a.Choice);
            return p;
        }

        private static readonly GraphicsProfile Show = Profile("Show",
            (TouchDesigner, GpuChoice.HighPerformance), (Chrome, GpuChoice.PowerSaving), (Explorer, GpuChoice.PowerSaving));

        private void BreakSaving() { Directory.CreateDirectory(StatePath + ".tmp"); }
        private void FixSaving() { Directory.Delete(StatePath + ".tmp"); }

#region Apply
        [Fact]
        public void Apply_ThenWindowsDefault_FromAnotherProcess_GivesTheOriginalsBack() {
            this.gpu.Values[Chrome] = "GpuPreference=2;";
            this.gpu.Values[TouchDesigner] = "SwapEffectUpgradeEnable=1;GpuPreference=0;";
            GraphicsResult r = NewApps().Apply(Show, "test");
            Assert.Null(r.Error);
            Assert.Equal(3, r.Done.Count);
            Assert.Equal("GpuPreference=1;", this.gpu.Values[Chrome]);
            Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=2;", this.gpu.Values[TouchDesigner]);
            Assert.Equal("GpuPreference=1;", this.gpu.Values[Explorer]);
            Assert.Equal(new[] { TouchDesigner, Chrome, Explorer }, r.Restart);

            // The CLI, or OmenMon after a restart, from the state file alone
            GraphicsResult back = NewApps().Apply(null!, "-Graphics Restore");
            Assert.Null(back.Error);
            Assert.Equal("GpuPreference=2;", this.gpu.Values[Chrome]);
            Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=0;", this.gpu.Values[TouchDesigner]);
            Assert.False(this.gpu.Values.ContainsKey(Explorer));
            Assert.Equal(new[] { TouchDesigner, Chrome, Explorer }, back.Restart);
            Assert.False(File.Exists(StatePath));
        }

        [Fact]
        public void Apply_Twice_ChangesNothingTheSecondTime() {
            NewApps().Apply(Show, "test");
            int writes = this.gpu.Writes;
            GraphicsResult r = NewApps().Apply(Show, "test");
            Assert.False(r.Plan.HasChanges);
            Assert.Empty(r.Done);
            Assert.Empty(r.Restart);
            Assert.Equal(writes, this.gpu.Writes);
        }

        // Each original is on disk before its app's value is changed
        [Fact]
        public void Apply_TheOriginalIsSavedBeforeEachWrite() {
            this.gpu.Values[Chrome] = "GpuPreference=2;";
            List<string> seen = new List<string>();
            this.gpu.BeforeWrite = app => {
                GraphicsState onDisk = GraphicsState.Load(StatePath);
                Assert.True(onDisk.Managed.ContainsKey(app), app + " written before its original was saved");
                seen.Add(app);
            };
            NewApps().Apply(Show, "test");
            Assert.Equal(3, seen.Count);
            Assert.Equal("GpuPreference=2;", GraphicsState.Load(StatePath).Managed[Chrome].Original);
            Assert.Null(GraphicsState.Load(StatePath).Managed[Explorer].Original);
        }

        [Fact]
        public void Apply_FailedSave_ChangesNothing_AndIsLogged() {
            this.gpu.Values[Chrome] = "GpuPreference=2;";
            var apps = NewApps();
            BreakSaving();
            GraphicsResult r = apps.Apply(Show, "test");
            Assert.Equal(0, this.gpu.Writes);
            Assert.Empty(r.Done);
            Assert.Empty(apps.State.Managed);
            Assert.Contains("could not save the original value", r.Error);
            Assert.Equal(r.Error, apps.LastError);
            Assert.Contains("could not save", LogText);

            FixSaving();
            Assert.Null(apps.Apply(Show, "test").Error);
            Assert.Equal("GpuPreference=1;", this.gpu.Values[Chrome]);
        }

        // A save that fails halfway leaves the apps after it as they are
        [Fact]
        public void Apply_SaveFailingAfterTheFirstApp_StopsThere() {
            var apps = NewApps();
            this.gpu.BeforeWrite = app => { if(app == TouchDesigner) BreakSaving(); };
            GraphicsResult r = apps.Apply(Show, "test");
            Assert.Single(r.Done);
            Assert.Equal("GpuPreference=2;", this.gpu.Values[TouchDesigner]);
            Assert.False(this.gpu.Values.ContainsKey(Chrome));
            Assert.False(this.gpu.Values.ContainsKey(Explorer));
            Assert.Equal(new[] { TouchDesigner }, apps.State.Managed.Keys);
            Assert.Contains("and the apps after it were left as they are", Assert.Single(r.Errors));
        }

        [Fact]
        public void Apply_FailedWrite_ForgetsThatApp_AndGoesOn() {
            this.gpu.Values[Chrome] = "GpuPreference=2;";
            this.gpu.FailOn = Chrome;
            GraphicsResult r = NewApps().Apply(Show, "test");
            Assert.Equal(2, r.Done.Count);
            Assert.Contains("could not set the GPU preference of " + Chrome, r.Error);
            Assert.Equal("GpuPreference=2;", this.gpu.Values[Chrome]);
            Assert.False(GraphicsState.Load(StatePath).Managed.ContainsKey(Chrome));
            Assert.Equal(new[] { TouchDesigner, Explorer }, r.Restart);
        }

        [Fact]
        public void Apply_FailedRestore_KeepsTheAppManaged_ForTheNextApply() {
            NewApps().Apply(Show, "test");
            this.gpu.FailOn = Explorer;
            GraphicsResult r = NewApps().Apply(null!, "test");
            Assert.Contains("could not restore the GPU preference of " + Explorer, r.Error);
            Assert.Equal(new[] { Explorer }, GraphicsState.Load(StatePath).Managed.Keys);
            this.gpu.FailOn = null;
            Assert.Null(NewApps().Apply(null!, "test").Error);
            Assert.Empty(this.gpu.Values);
            Assert.False(File.Exists(StatePath));
        }

        // Windows Settings changed it: left as it is, logged, the original forgotten
        [Fact]
        public void Apply_ChangedOutside_IsLeftAsItIs_AndLogged() {
            this.gpu.Values[Chrome] = "GpuPreference=2;";
            NewApps().Apply(Show, "test");
            this.gpu.Values[Chrome] = "GpuPreference=2;";
            GraphicsResult r = NewApps().Apply(Show, "test");
            GraphicsAction a = Assert.Single(r.Done);
            Assert.Equal(GraphicsActionKind.Unmanage, a.Kind);
            Assert.Equal("GpuPreference=2;", this.gpu.Values[Chrome]);
            Assert.Empty(r.Restart);
            Assert.Contains(Chrome + ": changed outside OmenMon", LogText);
            Assert.False(GraphicsState.Load(StatePath).Managed.ContainsKey(Chrome));
        }

        [Fact]
        public void Plan_ChangesNothing() {
            this.gpu.Values[Chrome] = "GpuPreference=2;";
            GraphicsPlan plan = NewApps().Plan(Show);
            Assert.Equal(3, plan.Changes.Count());
            Assert.Equal(0, this.gpu.Writes);
            Assert.False(File.Exists(StatePath));
        }

        [Fact]
        public void Log_SaysWhatWasDone() {
            this.gpu.Values[Chrome] = "GpuPreference=2;";
            NewApps().Apply(Show, "Power window");
            Assert.Contains("Graphics apps: Power window: applying Show", LogText);
            Assert.Contains(Chrome + ": high performance -> power saving (original kept)", LogText);
        }
#endregion

#region State
        [Fact]
        public void State_RoundTrip_AndGarbageIgnored() {
            var s = new GraphicsState(StatePath);
            s.Managed[Chrome] = new GraphicsManaged("GpuPreference=2;", "GpuPreference=1;");
            s.Managed[Terminal] = new GraphicsManaged(null, "GpuPreference=1;");
            Assert.True(s.Save());
            File.AppendAllText(StatePath, "gpu\tonly-two\r\ngpu\t\tx\ty\r\nnonsense\r\ngpu\tno-written.exe\tGpuPreference=2;\t\r\n");
            var t = GraphicsState.Load(StatePath);
            Assert.Equal(2, t.Managed.Count);
            Assert.Equal("GpuPreference=2;", t.Managed[Chrome].Original);
            Assert.Null(t.Managed[Terminal.ToLowerInvariant()].Original);
            Assert.Equal("GpuPreference=1;", t.Managed[Terminal].Written);
        }

        [Fact]
        public void State_Unreadable_IsKeptAsBad_AndLogged() {
            File.WriteAllText(StatePath, "gpu\ta.exe\t\tGpuPreference=1;\r\n");
            GraphicsState s;
            using(new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Delete))
                s = GraphicsState.Load(StatePath);
            Assert.True(s.IsEmpty);
            Assert.False(s.Blocked);
            Assert.Contains("a.exe", File.ReadAllText(StatePath + ".bad"));
            new GraphicsApps(this.gpu, s);
            Assert.Contains(".bad", LogText);
        }

        [Fact]
        public void State_UnreadableAndNotMovable_BlocksApply_UntilItCanBeRead() {
            File.WriteAllText(StatePath, "gpu\t" + Chrome + "\tGpuPreference=2;\tGpuPreference=1;\r\n");
            this.gpu.Values[Chrome] = "GpuPreference=1;";
            GraphicsApps apps;
            using(new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None)) {
                apps = NewApps();
                Assert.True(apps.State.Blocked);
                GraphicsResult r = apps.Apply(null!, "test");
                Assert.Contains("cannot be read", r.Error);
                Assert.Equal(0, this.gpu.Writes);
                Assert.Null(apps.Adopt("Show", null!));
            }
            Assert.Contains("Graphics apps changes nothing", LogText);
            Assert.Null(apps.Apply(null!, "test").Error);    // readable again: restored
            Assert.Equal("GpuPreference=2;", this.gpu.Values[Chrome]);
        }

        [Fact]
        public void State_InterruptedSave_IsRecoveredFromTheTemporaryFile() {
            File.WriteAllText(StatePath + ".tmp", "gpu\t" + Chrome + "\tGpuPreference=2;\tGpuPreference=1;\r\n");
            this.gpu.Values[Chrome] = "GpuPreference=1;";
            NewApps().Apply(null!, "test");
            Assert.Equal("GpuPreference=2;", this.gpu.Values[Chrome]);
            Assert.False(File.Exists(StatePath + ".tmp"));
        }

        // The tests never write the user's real preferences
        [Fact]
        public void RealStore_RefusesWritesInTests() {
            var real = new RegistryGpuPreferences();
            Assert.Throws<InvalidOperationException>(() => real.Set(@"C:\NoSuchApp\never.exe", "GpuPreference=1;"));
            Assert.Throws<InvalidOperationException>(() => real.Remove(@"C:\NoSuchApp\never.exe"));
        }
#endregion

#region Adopt
        [Fact]
        public void Adopt_ManagesTheApps_ChangesNoValue() {
            this.gpu.Values[Chrome] = "GpuPreference=1;";
            this.gpu.Values[Explorer] = "GpuPreference=1;";
            this.gpu.Values["DirectXUserGlobalSettings"] = "SwapEffectUpgradeEnable=1;";
            var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Chrome] = "GpuPreference=2;" };
            GraphicsProfile? show = NewApps().Adopt("Show", originals);
            Assert.NotNull(show);
            Assert.Equal(new[] { Chrome, Explorer }, show!.Apps.Select(a => a.Key));
            Assert.Equal(0, this.gpu.Writes);
            GraphicsState s = GraphicsState.Load(StatePath);
            Assert.Equal("GpuPreference=2;", s.Managed[Chrome].Original);
            Assert.Null(s.Managed[Explorer].Original);
            Assert.Contains("adopted profile Show: 2 app(s), originals from the .reg file", LogText);

            // Windows default then puts back what the export held
            NewApps().Apply(null!, "test");
            Assert.Equal("GpuPreference=2;", this.gpu.Values[Chrome]);
            Assert.False(this.gpu.Values.ContainsKey(Explorer));
        }

        [Fact]
        public void Adopt_AnAppManagedAlready_KeepsItsOriginal() {
            this.gpu.Values[Chrome] = "GpuPreference=2;";
            NewApps().Apply(Profile("P", (Chrome, GpuChoice.PowerSaving)), "test");
            NewApps().Adopt("Show", null!);
            Assert.Equal("GpuPreference=2;", GraphicsState.Load(StatePath).Managed[Chrome].Original);
        }

        [Fact]
        public void Adopt_FailedSave_AdoptsNothing() {
            this.gpu.Values[Chrome] = "GpuPreference=1;";
            var apps = NewApps();
            BreakSaving();
            Assert.Null(apps.Adopt("Show", null!));
            Assert.Contains("could not save the originals", apps.LastError);
            Assert.Empty(apps.State.Managed);
        }
#endregion

#region Moving Eco+ GPU preferences
        private EcoPlusState EcoWithGpuLines() {
            File.WriteAllText(EcoStatePath, "refresh\t165\t60\tpanel\r\ngpu\t" + Chrome + "\tGpuPreference=2;\r\ngpu\t" + Terminal + "\t\r\n");
            return EcoPlusState.Load(EcoStatePath);
        }

        [Fact]
        public void Migration_MovesTheLines_GraphicsFirst_ThenDropsThemFromEcoPlus() {
            EcoPlusState eco = EcoWithGpuLines();
            GraphicsState graphics = GraphicsState.Load(StatePath);
            string? done = GraphicsMigration.FromEcoPlus(eco, graphics, out string? error);
            Assert.Null(error);
            Assert.StartsWith("2 GPU preference(s) moved", done);

            GraphicsState g = GraphicsState.Load(StatePath);
            Assert.Equal("GpuPreference=2;", g.Managed[Chrome].Original);
            Assert.Equal(GraphicsMigration.EcoPlusWritten, g.Managed[Chrome].Written);
            Assert.Null(g.Managed[Terminal].Original);
            EcoPlusState e = EcoPlusState.Load(EcoStatePath);
            Assert.Empty(e.SavedGpu);
            Assert.Equal(165, e.RefreshBefore);                 // the rest of Eco+ stays
            Assert.Contains(Chrome + ": managed, original high performance [GpuPreference=2;], Eco+ had set power saving", LogText);

            // Run again (every start): nothing to do
            Assert.Null(GraphicsMigration.FromEcoPlus(EcoPlusState.Load(EcoStatePath), GraphicsState.Load(StatePath), out error));

            // And Windows default gives the originals back
            this.gpu.Values[Chrome] = "GpuPreference=1;";
            this.gpu.Values[Terminal] = "GpuPreference=1;";
            NewApps().Apply(null!, "test");
            Assert.Equal("GpuPreference=2;", this.gpu.Values[Chrome]);
            Assert.False(this.gpu.Values.ContainsKey(Terminal));
        }

        // A crash (or a failed save) between the two saves: the lines are in both files
        [Fact]
        public void Migration_LineInBoth_IsOnlyDroppedFromEcoPlus() {
            var g = new GraphicsState(StatePath);
            g.Managed[Chrome] = new GraphicsManaged("GpuPreference=0;", "GpuPreference=1;");   // moved before, original kept
            Assert.True(g.Save());
            string? done = GraphicsMigration.FromEcoPlus(EcoWithGpuLines(), GraphicsState.Load(StatePath), out string? error);
            Assert.Contains("1 GPU preference(s) moved", done);
            Assert.Contains("1 moved before", done);
            Assert.Equal("GpuPreference=0;", GraphicsState.Load(StatePath).Managed[Chrome].Original);
            Assert.Empty(EcoPlusState.Load(EcoStatePath).SavedGpu);
        }

        [Fact]
        public void Migration_GraphicsStateNotSaved_KeepsTheEcoPlusLines() {
            EcoPlusState eco = EcoWithGpuLines();
            GraphicsState graphics = GraphicsState.Load(StatePath);
            BreakSaving();
            Assert.Null(GraphicsMigration.FromEcoPlus(eco, graphics, out string? error));
            Assert.Contains("not moved", error);
            Assert.Empty(graphics.Managed);
            Assert.Equal(2, eco.SavedGpu.Count);

            // Eco+ saving meanwhile keeps them too, so the next start can move them
            eco.RefreshBefore = null;
            Assert.True(eco.Save());
            Assert.Equal(2, EcoPlusState.Load(EcoStatePath).SavedGpu.Count);
        }

        [Fact]
        public void Migration_EcoPlusStateNotSaved_IsFinishedOnTheNextStart() {
            EcoPlusState eco = EcoWithGpuLines();
            Directory.CreateDirectory(EcoStatePath + ".tmp");     // the Eco+ save fails
            string? done = GraphicsMigration.FromEcoPlus(eco, GraphicsState.Load(StatePath), out string? error);
            Assert.Contains("still lists them", done);
            Assert.Equal(2, EcoPlusState.Load(EcoStatePath).SavedGpu.Count);
            Directory.Delete(EcoStatePath + ".tmp");

            done = GraphicsMigration.FromEcoPlus(EcoPlusState.Load(EcoStatePath), GraphicsState.Load(StatePath), out error);
            Assert.StartsWith("0 GPU preference(s) moved", done);
            Assert.Empty(EcoPlusState.Load(EcoStatePath).SavedGpu);
            Assert.Equal(2, GraphicsState.Load(StatePath).Managed.Count);
        }

        [Fact]
        public void Migration_GraphicsStateBlocked_MovesNothing() {
            File.WriteAllText(StatePath, "gpu\ta.exe\t\tGpuPreference=1;\r\n");
            EcoPlusState eco = EcoWithGpuLines();
            using(new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None)) {
                GraphicsState graphics = GraphicsState.Load(StatePath);
                Assert.Null(GraphicsMigration.FromEcoPlus(eco, graphics, out string? error));
                Assert.Contains("cannot be read", error);
            }
            Assert.Equal(2, EcoPlusState.Load(EcoStatePath).SavedGpu.Count);
        }
#endregion

    }

}
