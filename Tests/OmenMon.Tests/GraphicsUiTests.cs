  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Library;

namespace OmenMon.Tests {

    // Graphics apps, the window's decisions (GraphicsUi.cs): editing and saving profiles,
    // the lists, the texts, which restart rows can be checked, the restart run with the
    // user's answers, the second confirmation before the session ends, the check after a
    // start and the Power window's choice of profile. Fakes only: no registry, no
    // process, no ExitWindowsEx.
    public class GraphicsUiTests : IDisposable {

        private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        private const string TouchDesigner = @"C:\Program Files\Derivative\TouchDesigner\bin\TouchDesigner.exe";
        private const string Terminal = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";

        private readonly string dir = Path.Combine(Path.GetTempPath(), "omenmon-graphics-ui-" + Guid.NewGuid().ToString("N"));

        public GraphicsUiTests() {
            Directory.CreateDirectory(this.dir);
        }

        public void Dispose() {
            try { Directory.Delete(this.dir, true); } catch { }
        }

        private static GraphicsProfile Profile(string name, params (string App, GpuChoice Choice)[] apps) {
            var p = new GraphicsProfile(name);
            foreach(var a in apps)
                p.Set(a.App, a.Choice);
            return p;
        }

#region Editor
        // The configuration as the editor sees it: profiles, the active one, whether the
        // file loaded, and the saves (a save can be made to fail)
        private sealed class FakeConfig {
            public readonly SortedList<string, GraphicsProfile> Profiles = new SortedList<string, GraphicsProfile>(StringComparer.OrdinalIgnoreCase);
            public string Active = "";
            public bool LoadFailed;
            public bool SaveFails;
            public int Saves;
            public GraphicsProfileEditor Editor() {
                return new GraphicsProfileEditor(() => this.Profiles, () => this.Active, a => this.Active = a, () => this.LoadFailed, () => {
                    if(this.SaveFails)
                        throw new IOException("disk full");
                    this.Saves++;
                });
            }
            public string Dump() {
                return string.Join("|", this.Profiles.Select(p => p.Key + ":" + string.Join(",", p.Value.Apps.Select(a => a.Key + "=" + a.Choice))))
                    + "|active=" + this.Active;
            }
        }

        private static FakeConfig WithShow() {
            FakeConfig c = new FakeConfig();
            c.Profiles["Show"] = Profile("Show", (TouchDesigner, GpuChoice.HighPerformance), (Chrome, GpuChoice.PowerSaving));
            c.Profiles["Battery"] = Profile("Battery", (Chrome, GpuChoice.PowerSaving));
            c.Active = "Show";
            return c;
        }

        [Fact]
        public void Editor_New_SavesOnce_AndRefusesBadOrTakenNames() {
            FakeConfig c = WithShow();
            GraphicsProfileEditor e = c.Editor();
            Assert.Null(e.New("Gig"));
            Assert.True(c.Profiles.ContainsKey("Gig"));
            Assert.Equal(1, c.Saves);
            Assert.NotNull(e.New("show"));          // taken, whatever the case
            Assert.NotNull(e.New(" Padded"));
            Assert.NotNull(e.New("Default"));
            Assert.NotNull(e.New(GraphicsProfile.WindowsDefaultName));
            Assert.NotNull(e.New("-x"));
            Assert.NotNull(e.New(""));
            Assert.Equal(1, c.Saves);
            Assert.Equal(3, c.Profiles.Count);
        }

        [Fact]
        public void Editor_WhileTheConfigDidNotLoad_ChangesAndSavesNothing() {
            FakeConfig c = WithShow();
            c.LoadFailed = true;
            string before = c.Dump();
            GraphicsProfileEditor e = c.Editor();
            Assert.False(e.CanEdit);
            Assert.Equal(GraphicsProfileEditor.LoadFailedText, e.New("Gig"));
            Assert.Equal(GraphicsProfileEditor.LoadFailedText, e.Duplicate("Show", "Show 2"));
            Assert.Equal(GraphicsProfileEditor.LoadFailedText, e.Rename("Battery", "Travel"));
            Assert.Equal(GraphicsProfileEditor.LoadFailedText, e.Delete("Battery"));
            Assert.Equal(GraphicsProfileEditor.LoadFailedText, e.AddApps("Show", new[] { Terminal }, GpuChoice.PowerSaving, out int added));
            Assert.Equal(0, added);
            Assert.Equal(GraphicsProfileEditor.LoadFailedText, e.SetChoice("Show", Chrome, GpuChoice.HighPerformance));
            Assert.Equal(GraphicsProfileEditor.LoadFailedText, e.RemoveApp("Show", Chrome));
            Assert.Equal(before, c.Dump());
            Assert.Equal(0, c.Saves);
        }

        [Fact]
        public void Editor_AFailedSave_PutsEverythingBack() {
            FakeConfig c = WithShow();
            c.SaveFails = true;
            string before = c.Dump();
            GraphicsProfileEditor e = c.Editor();
            Assert.Contains("disk full", e.Rename("Show", "Live"));
            Assert.Equal(before, c.Dump());
            Assert.Contains("disk full", e.SetChoice("Show", Chrome, GpuChoice.HighPerformance));
            Assert.Contains("disk full", e.AddApps("Show", new[] { Terminal }, GpuChoice.PowerSaving, out int added));
            Assert.Equal(0, added);
            Assert.Contains("disk full", e.Delete("Battery"));
            Assert.Contains("disk full", e.AfterApply(c.Profiles["Battery"], new GraphicsResult()));
            Assert.Equal(before, c.Dump());
        }

        [Fact]
        public void Editor_Rename_KeepsTheActiveProfileActive() {
            FakeConfig c = WithShow();
            GraphicsProfileEditor e = c.Editor();
            Assert.Null(e.Rename("Show", "Live"));
            Assert.Equal("Live", c.Active);
            Assert.False(c.Profiles.ContainsKey("Show"));
            Assert.Equal(GpuChoice.HighPerformance, c.Profiles["Live"].Get(TouchDesigner));
            Assert.NotNull(e.Rename("Live", "Battery"));    // taken
            Assert.Null(e.Rename("Battery", "BATTERY"));     // a change of case only
            Assert.True(c.Profiles.ContainsKey("BATTERY"));
            Assert.Equal("BATTERY", c.Profiles.Values.Single(p => p.Apps.Count == 1).Name);
            Assert.Equal("Live", c.Active);
        }

        [Fact]
        public void Editor_Delete_RefusesTheActiveProfile() {
            FakeConfig c = WithShow();
            GraphicsProfileEditor e = c.Editor();
            Assert.Contains("active profile", e.Delete("Show"));
            Assert.True(c.Profiles.ContainsKey("Show"));
            Assert.Equal(0, c.Saves);
            Assert.Null(e.Delete("Battery"));
            Assert.False(c.Profiles.ContainsKey("Battery"));
            Assert.Equal(1, c.Saves);
            Assert.NotNull(e.Delete("Nope"));
        }

        [Fact]
        public void Editor_Duplicate_CopiesTheApps_IndependentOfTheSource() {
            FakeConfig c = WithShow();
            GraphicsProfileEditor e = c.Editor();
            Assert.Equal("Show copy", e.FreeName("Show copy"));
            Assert.Null(e.Duplicate("Show", "Show copy"));
            Assert.Equal("Show copy 2", e.FreeName("Show copy"));
            Assert.Null(e.SetChoice("Show copy", Chrome, GpuChoice.Auto));
            Assert.Equal(GpuChoice.PowerSaving, c.Profiles["Show"].Get(Chrome));
            Assert.Equal(GpuChoice.Auto, c.Profiles["Show copy"].Get(Chrome));
            Assert.Equal("Show", c.Active);
        }

        [Fact]
        public void Editor_AddApps_LeavesAppsInTheProfileAsTheyAre() {
            FakeConfig c = WithShow();
            GraphicsProfileEditor e = c.Editor();
            Assert.Null(e.AddApps("Show", new[] { Chrome, Terminal, "" }, GpuChoice.HighPerformance, out int added));
            Assert.Equal(1, added);
            Assert.Equal(GpuChoice.PowerSaving, c.Profiles["Show"].Get(Chrome));
            Assert.Equal(GpuChoice.HighPerformance, c.Profiles["Show"].Get(Terminal));
            Assert.Null(e.RemoveApp("Show", Terminal));
            Assert.NotNull(e.RemoveApp("Show", Terminal));
            Assert.False(c.Profiles["Show"].Contains(Terminal));
        }

        [Fact]
        public void Editor_AfterApply_MarksActiveOnlyWhenAllOfItWasApplied() {
            FakeConfig c = WithShow();
            GraphicsProfileEditor e = c.Editor();
            GraphicsResult failed = new GraphicsResult();
            failed.Errors.Add("could not set");
            Assert.NotNull(e.AfterApply(c.Profiles["Battery"], failed));
            Assert.Equal("Show", c.Active);
            Assert.Equal(0, c.Saves);

            Assert.Null(e.AfterApply(c.Profiles["Battery"], new GraphicsResult()));
            Assert.Equal("Battery", c.Active);
            Assert.Equal(1, c.Saves);

            // The same again: nothing to save
            Assert.Null(e.AfterApply(c.Profiles["Battery"], new GraphicsResult()));
            Assert.Equal(1, c.Saves);

            // Windows default
            Assert.Null(e.AfterApply(null, new GraphicsResult()));
            Assert.Equal("", c.Active);
            Assert.Equal(2, c.Saves);

            // Applied, but the file did not load: not saved
            c.LoadFailed = true;
            Assert.Contains("did not load", e.AfterApply(c.Profiles["Show"], new GraphicsResult()));
            Assert.Equal("", c.Active);
            Assert.Equal(2, c.Saves);
        }
#endregion

#region Lists and texts
        [Fact]
        public void ChoiceLabels_RoundTrip() {
            foreach(GpuChoice choice in Enum.GetValues<GpuChoice>())
                Assert.Equal(choice, GraphicsChoiceLabel.Parse(GraphicsChoiceLabel.Of(choice)));
            Assert.Null(GraphicsChoiceLabel.Parse("Turbo"));
            Assert.Null(GraphicsChoiceLabel.Parse(null!));
            Assert.Equal(3, GraphicsChoiceLabel.All.Distinct().Count());
        }

        [Fact]
        public void ProfileList_WindowsDefaultFirst_ActiveMarked() {
            FakeConfig c = WithShow();
            List<GraphicsProfileItem> items = GraphicsLists.Profiles(c.Profiles, "show");
            Assert.Equal(new[] { GraphicsProfile.WindowsDefaultName, "Battery", "Show" }, items.Select(i => i.Name));
            Assert.True(items[0].IsWindowsDefault);
            Assert.Equal(new[] { false, false, true }, items.Select(i => i.Active));
            Assert.Contains("active", items[2].ToString());
            Assert.True(GraphicsLists.Profiles(c.Profiles, "")[0].Active);
            Assert.DoesNotContain(GraphicsLists.Profiles(c.Profiles, "Gone"), i => i.Active);
        }

        [Fact]
        public void AppRows_ShowTheRegistryValueNow() {
            Dictionary<string, string> now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                [Chrome] = "GpuPreference=2;", [TouchDesigner] = "SwapEffectUpgradeEnable=1;" };
            List<GraphicsAppRow> rows = GraphicsLists.ForProfile(WithShow().Profiles["Show"], k => now.TryGetValue(k, out string? v) ? v : null!);
            Assert.Equal(new[] { "TouchDesigner.exe", "chrome.exe" }, rows.Select(r => r.Name));
            Assert.Equal(new[] { GraphicsChoiceLabel.HighPerformance, GraphicsChoiceLabel.PowerSaving }, rows.Select(r => r.Choice));
            Assert.Equal(new[] { "default", "high performance" }, rows.Select(r => r.Now));
            Assert.All(rows, r => Assert.True(r.Editable));

            Dictionary<string, GraphicsManaged> managed = new Dictionary<string, GraphicsManaged>(StringComparer.OrdinalIgnoreCase) {
                [Chrome] = new GraphicsManaged("GpuPreference=2;", "GpuPreference=1;"),
                [Terminal] = new GraphicsManaged(null, "GpuPreference=1;") };
            List<GraphicsAppRow> back = GraphicsLists.ForWindowsDefault(managed, k => throw new UnauthorizedAccessException());
            Assert.Equal(new[] { "chrome.exe", "Microsoft.WindowsTerminal" }, back.Select(r => r.Name));
            Assert.Equal(new[] { "back to high performance", "back to no preference" }, back.Select(r => r.Choice));
            Assert.All(back, r => Assert.False(r.Editable));
            Assert.All(back, r => Assert.Equal("default", r.Now));   // unreadable: shown as none
        }

        [Fact]
        public void AppNames() {
            Assert.Equal("chrome.exe", GraphicsLists.Name(Chrome));
            Assert.Equal("Microsoft.WindowsTerminal", GraphicsLists.Name(Terminal));
            Assert.Equal("Microsoft.ZuneMusic (Microsoft.ZuneMusic)", GraphicsLists.Name("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"));
            Assert.Equal("odd", GraphicsLists.Name("odd"));
            Assert.Equal("?", GraphicsLists.Name(""));
        }

        [Fact]
        public void RunningApps_OnePerApp_NoOmenMon_NoWindowsComponents() {
            List<RunningApp> found = new List<RunningApp> {
                new RunningApp { Pid = 10, Key = Chrome, Path = Chrome },
                new RunningApp { Pid = 11, Key = Chrome.ToUpperInvariant(), Path = Chrome },
                new RunningApp { Pid = 12, Key = @"C:\Windows\System32\dwm.exe", Path = @"C:\Windows\System32\dwm.exe" },
                new RunningApp { Pid = 13, Key = @"C:\OmenMon\OmenMon.exe", Path = @"C:\OmenMon\OmenMon.exe" },
                new RunningApp { Pid = 14, Key = Terminal, Path = @"C:\Program Files\WindowsApps\Terminal\WindowsTerminal.exe", Name = "Terminal" },
                new RunningApp { Pid = 15, Key = null!, Path = @"C:\x.exe" },
                new RunningApp { Pid = 16, Key = TouchDesigner, Path = TouchDesigner } };
            List<RunningApp> apps = GraphicsLists.Running(found, 13, p => p != null && p.StartsWith(@"C:\Windows\System32\", StringComparison.OrdinalIgnoreCase),
                new[] { TouchDesigner.ToLowerInvariant() }, WithShow().Profiles["Battery"]);
            Assert.Equal(new[] { "chrome.exe", "Terminal", "TouchDesigner.exe" }, apps.Select(a => a.Name));
            Assert.Equal(new[] { false, false, true }, apps.Select(a => a.HoldsGpu));
            Assert.Equal(new[] { true, false, false }, apps.Select(a => a.InProfile));
        }

        // A plan from the planner itself, for the texts
        private static GraphicsPlan PlanOf(GraphicsProfile? target, Dictionary<string, string> now, Dictionary<string, GraphicsManaged> managed) {
            return GraphicsPlanner.Plan(target, now, managed);
        }

        [Fact]
        public void PlanSummary_NamesTheWrites_AndWhatWasChangedInWindowsSettings() {
            Dictionary<string, string> now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                [Chrome] = "GpuPreference=2;", [TouchDesigner] = "GpuPreference=1;" };
            Dictionary<string, GraphicsManaged> managed = new Dictionary<string, GraphicsManaged>(StringComparer.OrdinalIgnoreCase) {
                [Chrome] = new GraphicsManaged(null, "GpuPreference=1;") };    // the user set it back to 2
            GraphicsPlan plan = PlanOf(WithShow().Profiles["Show"], now, managed);
            string text = GraphicsUiText.PlanSummary(plan);
            Assert.Contains("Apply Show", text);
            Assert.Contains(TouchDesigner + ": power saving -> high performance", text);
            Assert.Contains("Changed in Windows Settings: left as it is", text);
            Assert.Contains(Chrome + ": now high performance, OmenMon had set power saving", text);
            Assert.Contains("1 app(s) get another GPU", text);

            GraphicsPlan nothing = PlanOf(null, now, new Dictionary<string, GraphicsManaged>());
            Assert.Contains("Nothing to change", GraphicsUiText.PlanSummary(nothing));
            Assert.Contains("Windows default", GraphicsUiText.PlanSummary(nothing));
        }

        [Fact]
        public void GpuStatus_Texts() {
            Func<string, bool> system = p => p != null && p.Contains(@"\System32\");
            Assert.Equal("Discrete GPU: off (D3): no app keeps it awake", GraphicsUiText.GpuStatus(4, false, null!, system));
            Assert.Equal("Discrete GPU: unknown", GraphicsUiText.GpuStatus(-1, false, null!, system));
            Assert.Contains("could not be read", GraphicsUiText.GpuStatus(1, false, null!, system));
            Assert.Contains("no app holds it", GraphicsUiText.GpuStatus(1, true, new List<GpuHolder>(), system));
            string held = GraphicsUiText.GpuStatus(1, true, new[] {
                new GpuHolder { Pid = 7, Name = "chrome", Path = Chrome },
                new GpuHolder { Pid = 8, Name = "dwm", Path = @"C:\Windows\System32\dwm.exe" } }, system);
            Assert.Equal("Discrete GPU: on (D0), held by: chrome (7) and 1 Windows component(s)", held);
        }

        [Fact]
        public void SessionConfirmations_NameWhatHappens() {
            string signOut = GraphicsUiText.SessionConfirmation(SessionEnd.SignOut, "Show");
            Assert.StartsWith("Sign out of Windows now?", signOut);
            Assert.Contains("closes every app", signOut);
            Assert.Contains("OmenMon exits too", signOut);
            Assert.Contains("BIOS", signOut);
            Assert.Contains("of Show", signOut);
            string restart = GraphicsUiText.SessionConfirmation(SessionEnd.RestartWindows, "");
            Assert.StartsWith("Restart Windows now?", restart);
            Assert.Contains("after the restart", restart);
        }
#endregion

#region Restart dialog
        private static ProcessEntry Proc(int pid, string path, bool? elevated = false, bool window = true, string? commandLine = "\"x\"") {
            return new ProcessEntry { Pid = pid, ParentPid = 1, CreationTime = 1000 + pid, ImagePath = path, SessionId = 1,
                Elevated = elevated, InteractiveUser = true, HasVisibleWindow = window, CommandLine = commandLine };
        }

        private static ProcessSnapshot Snapshot(params ProcessEntry[] processes) {
            return new ProcessSnapshot { OwnPid = 99, InteractiveSession = 1, Processes = processes.ToList() };
        }

        [Fact]
        public void RestartRows_OnlyRestartableAppsCanBeChecked() {
            List<RestartEntry> entries = RestartPlanner.Plan(new[] { Chrome, TouchDesigner, @"C:\Windows\explorer.exe" }, Snapshot(
                Proc(10, Chrome), Proc(11, Chrome), Proc(20, TouchDesigner, elevated: true), Proc(30, @"C:\Windows\explorer.exe")));
            List<RestartRow> rows = GraphicsRestartRows.Build(entries, null!);
            Assert.Equal(new[] { "chrome.exe", "TouchDesigner.exe", "explorer.exe" }, rows.Select(r => r.Name));
            Assert.Equal(new[] { 2, 1, 1 }, rows.Select(r => r.Processes));
            Assert.Equal(new[] { true, false, false }, rows.Select(r => r.Checkable));
            Assert.Equal(new[] { true, false, false }, rows.Select(r => r.Checked));
            Assert.Equal(RestartText.Reason(RestartBlock.Elevated), rows[1].Note);
            Assert.Equal(RestartText.Reason(RestartBlock.Explorer), rows[2].Note);

            // A row forced checked that cannot be restarted is still not confirmed
            rows[1].Checked = true;
            rows[2].Checked = true;
            Assert.Equal(new[] { Chrome }, GraphicsRestartRows.Confirmed(rows).Select(e => e.AppKey));
            rows[0].Checked = false;
            Assert.Empty(GraphicsRestartRows.Confirmed(rows));
        }

        [Fact]
        public void RestartRows_ArgumentsUnknown_AndNoWindow_AreNoted() {
            List<RestartEntry> entries = RestartPlanner.Plan(new[] { Chrome }, Snapshot(Proc(10, Chrome, window: false, commandLine: null)));
            RestartRow row = GraphicsRestartRows.Build(entries, null!).Single();
            Assert.True(row.Checkable);
            Assert.Contains("arguments unknown", row.Note);
            Assert.Contains("no window", row.Note);
        }

        [Fact]
        public void RestartRows_WhenTheAppsCannotBeListed_NoneCanBeChecked() {
            List<RestartRow> rows = GraphicsRestartRows.Build(null!, new[] { Chrome, Terminal });
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => { Assert.False(r.Checkable); Assert.False(r.Checked); Assert.Null(r.Entry); });
            Assert.Equal(GraphicsRestartRows.CannotList, rows[0].Note);
            rows[0].Checked = true;
            Assert.Empty(GraphicsRestartRows.Confirmed(rows));
            Assert.True(GraphicsRestartRows.Show(null!, new[] { Chrome }));
            Assert.False(GraphicsRestartRows.Show(new List<RestartEntry>(), new[] { Chrome }));
            Assert.False(GraphicsRestartRows.Show(null!, new string[0]));
        }

        [Fact]
        public void RestartRun_AsksForEveryAppStillRunning_AndPassesTheAnswerOn() {
            List<RestartEntry> confirmed = RestartPlanner.Plan(new[] { Chrome, TouchDesigner }, Snapshot(Proc(10, Chrome), Proc(20, TouchDesigner)));
            List<string> asked = new List<string>(), log = new List<string>(), progress = new List<string>();
            List<(string, bool)> decided = new List<(string, bool)>();
            List<RestartResult> results = GraphicsRestartRun.Run(confirmed,
                e => e.AppKey == Chrome
                    ? new RestartResult { AppKey = e.AppKey, Entry = e, Outcome = RestartOutcome.NeedsDecision, Detail = "still running after 15 s" }
                    : new RestartResult { AppKey = e.AppKey, Entry = e, Outcome = RestartOutcome.Restarted },
                (r, end) => { decided.Add((r.AppKey, end)); return new RestartResult { AppKey = r.AppKey, Outcome = end ? RestartOutcome.Restarted : RestartOutcome.Skipped }; },
                r => { asked.Add(GraphicsUiText.DecisionQuestion(r)); return false; },
                (i, n, e) => progress.Add(i + "/" + n + " " + e?.DisplayName),
                log.Add);
            Assert.Equal(new[] { RestartOutcome.Skipped, RestartOutcome.Restarted }, results.Select(r => r.Outcome));
            Assert.Single(asked);
            Assert.StartsWith("chrome.exe is still running after 15 s", asked[0]);
            Assert.Contains("End it", asked[0]);
            Assert.Equal(new[] { (Chrome, false) }, decided);
            Assert.Equal(new[] { "0/2 chrome.exe", "1/2 TouchDesigner.exe", "2/2 " }, progress);
            Assert.Contains(log, l => l.Contains("chrome.exe: the user chose to leave it running"));
        }

        [Fact]
        public void RestartRun_AFailingRestart_IsReported_NotThrown() {
            List<RestartEntry> confirmed = RestartPlanner.Plan(new[] { Chrome }, Snapshot(Proc(10, Chrome)));
            List<RestartResult> results = GraphicsRestartRun.Run(confirmed, e => throw new InvalidOperationException("boom"),
                (r, end) => throw new InvalidOperationException("never"), r => throw new InvalidOperationException("never"), null!, null!);
            Assert.Equal(RestartOutcome.LaunchFailed, results.Single().Outcome);
            Assert.Contains("boom", results.Single().Detail);
        }

        // The real coordinator with fakes: an app that does not close is ended only on
        // the user's word
        private sealed class FakeProcesses : IProcessCatalog, IProcessCloser, IUnelevatedLauncher {
            public readonly List<ProcessEntry> Running = new List<ProcessEntry>();
            public readonly List<int> Ended = new List<int>();
            public int Starts;
            public ProcessSnapshot Snapshot() { return new ProcessSnapshot { OwnPid = 99, InteractiveSession = 1, Processes = this.Running.ToList() }; }
            public bool RequestClose(ProcessEntry p) { return true; }      // asked, but it stays
            public bool WaitForExit(ProcessEntry p, TimeSpan timeout) { return !this.Running.Any(r => r.Pid == p.Pid); }
            public bool EndProcess(ProcessEntry p) { this.Ended.Add(p.Pid); this.Running.RemoveAll(r => r.Pid == p.Pid); return true; }
            public LaunchResult Check() { return LaunchResult.Of(LaunchStatus.Ready); }
            public LaunchResult Start(string imagePath, string commandLine, string workingDirectory) { this.Starts++; return LaunchResult.Of(LaunchStatus.Started, pid: 500); }
            public LaunchResult StartPackaged(string aumid) { this.Starts++; return LaunchResult.Of(LaunchStatus.Started, pid: 501); }
        }

        private static RestartCoordinator Coordinator(FakeProcesses f) {
            DateTime now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
            return new RestartCoordinator(f, f, f, sleep: t => now += t, clock: () => now);
        }

        [Fact]
        public void RestartRun_WithTheCoordinator_LeaveItRunning_EndsNothing() {
            FakeProcesses f = new FakeProcesses();
            f.Running.Add(Proc(10, Chrome));
            RestartCoordinator c = Coordinator(f);
            List<RestartEntry> planned = c.Plan(new[] { Chrome });
            List<RestartRow> rows = GraphicsRestartRows.Build(planned, new[] { Chrome });
            int asked = 0;
            List<RestartResult> results = GraphicsRestartRun.Run(GraphicsRestartRows.Confirmed(rows), c.Restart, c.Decide,
                r => { asked++; return false; }, null!, null!);
            Assert.Equal(1, asked);
            Assert.Equal(RestartOutcome.Skipped, results.Single().Outcome);
            Assert.Empty(f.Ended);
            Assert.Equal(0, f.Starts);
            Assert.Single(f.Running);
        }

        [Fact]
        public void RestartRun_WithTheCoordinator_EndIt_EndsAndStartsItAgain() {
            FakeProcesses f = new FakeProcesses();
            f.Running.Add(Proc(10, Chrome));
            RestartCoordinator c = Coordinator(f);
            List<RestartResult> results = GraphicsRestartRun.Run(c.Plan(new[] { Chrome }), c.Restart, c.Decide, r => true, null!, null!);
            Assert.Equal(new[] { 10 }, f.Ended);
            Assert.Equal(1, f.Starts);
            // The fake never shows the new process
            Assert.Equal(RestartOutcome.NotSeenRunning, results.Single().Outcome);
        }
#endregion

#region Ending the session
        private sealed class FakeSession : ISessionControl {
            public readonly List<SessionEnd> Calls = new List<SessionEnd>();
            public bool Refuse;
            public bool End(SessionEnd how, out string error) {
                this.Calls.Add(how);
                error = this.Refuse ? "Windows refused (error 5)" : null!;
                return !this.Refuse;
            }
        }

        [Fact]
        public void Session_EndsOnlyAfterTheSecondConfirmation() {
            FakeSession s = new FakeSession();
            List<string> shown = new List<string>(), log = new List<string>();
            Assert.False(GraphicsSession.Run(SessionEnd.SignOut, "Show", t => { shown.Add(t); return false; }, s, log.Add, out string? error));
            Assert.Null(error);
            Assert.Empty(s.Calls);
            Assert.Single(shown);
            Assert.Equal(GraphicsUiText.SessionConfirmation(SessionEnd.SignOut, "Show"), shown[0]);
            Assert.Contains("sign out: cancelled", log);

            Assert.True(GraphicsSession.Run(SessionEnd.RestartWindows, "Show", t => true, s, log.Add, out error));
            Assert.Equal(new[] { SessionEnd.RestartWindows }, s.Calls);
            Assert.Contains("restart Windows: confirmed", log);

            s.Refuse = true;
            Assert.False(GraphicsSession.Run(SessionEnd.SignOut, "Show", t => true, s, log.Add, out error));
            Assert.Contains("error 5", error);

            // No way to confirm: nothing happens
            Assert.False(GraphicsSession.Run(SessionEnd.SignOut, "Show", null!, s, log.Add, out error));
            Assert.Equal(2, s.Calls.Count);
        }
#endregion

#region Check after a start
        private sealed class FakeGpu : IGpuPreferences {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public int Writes;
            public IList<string> Apps() { return this.Values.Keys.ToList(); }
            public string Get(string app) { return this.Values.TryGetValue(app, out string? v) ? v : null!; }
            public void Set(string app, string value) { this.Writes++; this.Values[app] = value; }
            public void Remove(string app) { this.Writes++; this.Values.Remove(app); }
        }

        [Fact]
        public void StartupCheck_ComparesWithTheActiveProfile_AndChangesNothing() {
            FakeGpu gpu = new FakeGpu();
            FakeConfig c = WithShow();
            GraphicsApps apps = new GraphicsApps(gpu, GraphicsState.Load(Path.Combine(this.dir, GraphicsState.FileName)));
            Assert.Null(apps.Apply(c.Profiles["Show"], "test").Error);
            int writes = gpu.Writes;

            GraphicsDrift same = GraphicsStartupCheck.Check(c.Profiles, "Show", apps.Plan)!;
            Assert.Equal(0, same.Apps);
            Assert.Equal("registry matches the active profile Show", same.Text);

            // Windows Settings changed one, something else removed another
            gpu.Values[Chrome] = "GpuPreference=2;";
            gpu.Values.Remove(TouchDesigner);
            GraphicsDrift drift = GraphicsStartupCheck.Check(c.Profiles, "show", apps.Plan)!;
            Assert.Equal(2, drift.Apps);
            Assert.Equal("registry differs from the active profile Show: 2 app(s)", drift.Text);
            Assert.Equal(writes, gpu.Writes);

            Assert.Null(GraphicsStartupCheck.Check(c.Profiles, "", apps.Plan));
            Assert.Null(GraphicsStartupCheck.Check(c.Profiles, "Gone", apps.Plan));
            Assert.Equal(writes, gpu.Writes);
        }
#endregion

#region Power window
        [Fact]
        public void PowerTarget_NoChoiceWithOneProfileOrNone() {
            FakeConfig c = new FakeConfig();
            Assert.Null(GraphicsPowerTarget.Choices(c.Profiles, "", out _));
            c.Profiles["Show"] = Profile("Show");
            Assert.Null(GraphicsPowerTarget.Choices(c.Profiles, "Show", out _));
        }

        [Fact]
        public void PowerTarget_PicksTheActiveProfileFirst() {
            FakeConfig c = WithShow();
            List<string> choices = GraphicsPowerTarget.Choices(c.Profiles, "show", out int selected)!;
            Assert.Equal(new[] { "Battery", "Show", GraphicsPowerTarget.NewPowerSaving }, choices);
            Assert.Equal(1, selected);
            Assert.Equal("Battery", GraphicsPowerTarget.AddTarget("Battery"));
            Assert.Equal("", GraphicsPowerTarget.AddTarget(GraphicsPowerTarget.NewPowerSaving));
            Assert.Contains("only the apps added change", GraphicsPowerTarget.Note("Show", "Show"));
            Assert.Contains("all of it is applied", GraphicsPowerTarget.Note("Battery", "Show"));
            Assert.Contains("apps of Show not in it get their originals back", GraphicsPowerTarget.Note("Battery", "Show"));

            // None active: "Power saving", made from what OmenMon manages
            GraphicsPowerTarget.Choices(c.Profiles, "", out selected);
            Assert.Equal(2, selected);
            c.Profiles[GraphicsProfile.PowerSavingName] = Profile(GraphicsProfile.PowerSavingName);
            choices = GraphicsPowerTarget.Choices(c.Profiles, "", out selected)!;
            Assert.DoesNotContain(GraphicsPowerTarget.NewPowerSaving, choices);
            Assert.Equal(GraphicsProfile.PowerSavingName, choices[selected]);
        }

        [Fact]
        public void PowerTarget_TheNewPowerSavingChoice_GoesToAddApps() {
            // The choice "Power saving (new)" takes the path AddApps takes with no active
            // profile: made with the apps OmenMon manages, so applying it changes only
            // the apps added
            FakeConfig c = WithShow();
            Dictionary<string, GraphicsManaged> managed = new Dictionary<string, GraphicsManaged>(StringComparer.OrdinalIgnoreCase) {
                [TouchDesigner] = new GraphicsManaged(null, "GpuPreference=2;") };
            GraphicsProfile p = GraphicsProfile.AddApps(c.Profiles, GraphicsPowerTarget.AddTarget(GraphicsPowerTarget.NewPowerSaving),
                new[] { Terminal }, GpuChoice.PowerSaving, managed);
            Assert.Equal(GraphicsProfile.PowerSavingName, p.Name);
            Assert.Equal(GpuChoice.HighPerformance, p.Get(TouchDesigner));
            Assert.Equal(GpuChoice.PowerSaving, p.Get(Terminal));
        }
#endregion

    }

}
