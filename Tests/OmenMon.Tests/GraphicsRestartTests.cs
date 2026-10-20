  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System.Text.RegularExpressions;
using OmenMon.Library;

namespace OmenMon.Tests {

    // Graphics apps, process side: the restart list, polite closing, and starting apps
    // again never elevated. Fakes only: no real process is closed or started.
    public class GraphicsRestartTests {

        private const int Session = 1;
        private const int ShellPid = 1000;
        private const int OwnPid = 4242;

        private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        private const string Touch = @"C:\Program Files\Derivative\TouchDesigner\bin\TouchDesigner.exe";
        private const string Explorer = @"C:\Windows\explorer.exe";
        private const string Container = @"C:\Windows\System32\DriverStore\FileRepository\nv\Display.NvContainer\NVDisplay.Container.exe";
        private const string TerminalAumid = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";
        private const string TerminalExe = @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.0.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe";

        private DateTime now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        private readonly FakeCatalog catalog = new FakeCatalog();
        private readonly FakeCloser closer;
        private readonly FakeLauncher launcher;
        private readonly List<string> log = new List<string>();
        private int nextPid = 9000;

        public GraphicsRestartTests() {
            this.closer = new FakeCloser(this);
            this.launcher = new FakeLauncher(this);
            Add(new ProcessEntry { Pid = ShellPid, ParentPid = 900, ImagePath = Explorer, CommandLine = "C:\\WINDOWS\\Explorer.EXE", HasVisibleWindow = true });
        }

#region Fakes
        private sealed class FakeCatalog : IProcessCatalog {
            public readonly List<ProcessEntry> Running = new List<ProcessEntry>();
            public bool Unavailable;
            public int Snapshots;
            public ProcessSnapshot Snapshot() {
                this.Snapshots++;
                if(this.Unavailable) return null!;
                return new ProcessSnapshot { OwnPid = OwnPid, InteractiveSession = Session, Processes = this.Running.ToList() };
            }
            public bool IsRunning(int pid) { return this.Running.Any(p => p.Pid == pid); }
            public void Remove(int pid) { this.Running.RemoveAll(p => p.Pid == pid); }
        }

        // Windowed processes exit (with their app) once asked to close, unless stubborn
        private sealed class FakeCloser : IProcessCloser {
            private readonly GraphicsRestartTests t;
            public readonly HashSet<int> Stubborn = new HashSet<int>();
            public readonly HashSet<int> Unkillable = new HashSet<int>();
            public readonly List<int> Asked = new List<int>();
            public readonly List<int> Ended = new List<int>();
            private bool closeRequested;
            public FakeCloser(GraphicsRestartTests t) { this.t = t; }
            public bool RequestClose(ProcessEntry p) {
                this.Asked.Add(p.Pid);
                if(!p.HasVisibleWindow) return false;
                this.closeRequested = true;
                return true;
            }
            public bool WaitForExit(ProcessEntry p, TimeSpan timeout) {
                if(!this.t.catalog.IsRunning(p.Pid)) return true;
                if(this.Stubborn.Contains(p.Pid) || !this.closeRequested) {
                    this.t.now += timeout;
                    return false;
                }
                this.t.catalog.Remove(p.Pid);
                return true;
            }
            public bool EndProcess(ProcessEntry p) {
                this.Ended.Add(p.Pid);
                if(this.Unkillable.Contains(p.Pid)) return false;
                this.t.catalog.Remove(p.Pid);
                this.Stubborn.Remove(p.Pid);
                return true;
            }
        }

        // Records the launches; a started app appears in the catalog as a new process
        private sealed class FakeLauncher : IUnelevatedLauncher {
            private readonly GraphicsRestartTests t;
            public LaunchStatus CheckStatus = LaunchStatus.Ready;
            public LaunchStatus StartStatus = LaunchStatus.Started;
            public bool? NewElevated = false;
            public bool AppAppears = true;
            public readonly List<(string Image, string CommandLine, string Cwd)> Starts = new List<(string, string, string)>();
            public readonly List<string> Packaged = new List<string>();
            public int Checks;
            public FakeLauncher(GraphicsRestartTests t) { this.t = t; }
            public LaunchResult Check() { this.Checks++; return LaunchResult.Of(this.CheckStatus); }
            public LaunchResult Start(string imagePath, string commandLine, string workingDirectory) {
                this.Starts.Add((imagePath, commandLine, workingDirectory));
                return Started(new ProcessEntry { ImagePath = imagePath, CommandLine = commandLine });
            }
            public LaunchResult StartPackaged(string aumid) {
                this.Packaged.Add(aumid);
                return Started(new ProcessEntry { ImagePath = TerminalExe, Aumid = aumid });
            }
            private LaunchResult Started(ProcessEntry p) {
                if(this.StartStatus != LaunchStatus.Started) return LaunchResult.Of(this.StartStatus);
                p.Pid = this.t.nextPid++;
                p.ParentPid = ShellPid;
                p.CreationTime = 100000 + p.Pid;
                p.SessionId = Session;
                p.InteractiveUser = true;
                p.Elevated = this.NewElevated;
                if(this.AppAppears) this.t.catalog.Running.Add(p);
                return LaunchResult.Of(LaunchStatus.Started, null, p.Pid);
            }
        }

        // The launcher's Win32 calls: handles are counters, every one opened must be closed
        private sealed class FakeLaunchApi : ILaunchApi {
            public bool ShellFound = true;
            public int ShellSession = Session, Own = Session;
            public bool? ShellElevated = false, CopyElevated = false, ProcessElevated = false;
            public bool CreateOk = true, ResumeOk = true;
            public int Created, Resumed, Terminated, Duplicated;
            public string? Image, CommandLine;
            private int handles;
            public readonly HashSet<IntPtr> Open = new HashSet<IntPtr>();
            private IntPtr NewHandle() { IntPtr h = new IntPtr(++this.handles * 4); this.Open.Add(h); return h; }
            private IntPtr shell, copy;
            public string ExplorerPath { get { return Explorer; } }
            public int OwnSession() { return this.Own; }
            public bool OpenShellToken(out IntPtr token, out int session) {
                session = this.ShellSession;
                token = this.ShellFound ? (this.shell = NewHandle()) : IntPtr.Zero;
                return this.ShellFound;
            }
            public bool? IsElevated(IntPtr token) { return token == this.copy ? this.CopyElevated : this.ShellElevated; }
            public IntPtr DuplicatePrimary(IntPtr token) { this.Duplicated++; return this.copy = NewHandle(); }
            public bool CreateSuspended(IntPtr primaryToken, string imagePath, string commandLine, string workingDirectory, out LaunchedProcess process, out int error) {
                this.Created++;
                this.Image = imagePath;
                this.CommandLine = commandLine;
                error = this.CreateOk ? 0 : 740;
                process = this.CreateOk ? new LaunchedProcess { Process = NewHandle(), Thread = NewHandle(), Pid = 777 } : new LaunchedProcess();
                return this.CreateOk;
            }
            public bool? IsProcessElevated(IntPtr process) { return this.ProcessElevated; }
            public bool Resume(IntPtr thread) { this.Resumed++; return this.ResumeOk; }
            public bool Terminate(IntPtr process) { this.Terminated++; return true; }
            public void Close(IntPtr handle) { if(handle != IntPtr.Zero) this.Open.Remove(handle); }
        }
#endregion

#region Helpers
        // A process of the interactive user, unelevated, unless set otherwise
        private ProcessEntry Add(ProcessEntry p) {
            if(p.CreationTime == 0) p.CreationTime = 1000 + p.Pid;
            if(p.SessionId < 0) p.SessionId = Session;
            if(p.InteractiveUser == null) p.InteractiveUser = true;
            if(p.Elevated == null) p.Elevated = false;
            this.catalog.Running.Add(p);
            return p;
        }

        // Chrome: the browser (started by Explorer, the only one with windows) and its helpers
        private void AddChrome() {
            Add(new ProcessEntry { Pid = 2000, ParentPid = ShellPid, ImagePath = Chrome, CommandLine = "\"" + Chrome + "\" --profile-directory=Default", HasVisibleWindow = true });
            Add(new ProcessEntry { Pid = 2001, ParentPid = 2000, ImagePath = Chrome, CommandLine = "\"" + Chrome + "\" --type=renderer --renderer-client-id=5" });
            Add(new ProcessEntry { Pid = 2002, ParentPid = 2000, ImagePath = Chrome, CommandLine = "\"" + Chrome + "\" --type=gpu-process" });
            Add(new ProcessEntry { Pid = 2003, ParentPid = 2000, ImagePath = Chrome, CommandLine = "\"" + Chrome + "\" --type=crashpad-handler" });
        }

        private RestartCoordinator NewCoordinator() {
            return new RestartCoordinator(this.catalog, this.closer, this.launcher,
                t => this.now += t, () => this.now, s => this.log.Add(s));
        }

        private List<RestartEntry> Plan(params string[] keys) {
            return RestartPlanner.Plan(keys, this.catalog.Snapshot());
        }

        private RestartEntry Only(params string[] keys) {
            return Assert.Single(Plan(keys));
        }
#endregion

#region Planner
        [Fact]
        public void Plan_ChromesProcessesAreOneEntry_StartedAgainWithTheBrowsersCommandLine() {
            AddChrome();
            RestartEntry e = Only(Chrome);
            Assert.Equal(new[] { 2000, 2001, 2002, 2003 }, e.Processes.Select(p => p.Pid));
            Assert.Equal(2000, Assert.Single(e.TopLevel).Pid);
            LaunchSpec l = Assert.Single(e.Launches);
            Assert.Equal(Chrome, l.ImagePath);
            Assert.Equal("\"" + Chrome + "\" --profile-directory=Default", l.CommandLine);
            Assert.Equal(@"C:\Program Files\Google\Chrome\Application", l.WorkingDirectory);
            Assert.True(e.Restartable);
            Assert.False(e.Packaged);
            Assert.True(e.HasVisibleWindow);
            Assert.Equal("chrome.exe", e.DisplayName);
        }

        [Fact]
        public void Plan_MatchesPathsIgnoringCaseSlashesAndLongPathPrefix() {
            AddChrome();
            Assert.Single(Plan(Chrome.ToUpperInvariant()));
            Assert.Single(Plan(Chrome.Replace('\\', '/')));
            Assert.Single(Plan(@"\\?\" + Chrome));
        }

        [Fact]
        public void Plan_AppsNotRunningAreLeftOut_AndEachProcessGoesToOneApp() {
            AddChrome();
            List<RestartEntry> entries = Plan(Touch, Chrome, Chrome.ToLowerInvariant());
            Assert.Equal(Chrome, Assert.Single(entries).AppKey);
            Assert.Empty(Plan());
            Assert.Empty(RestartPlanner.Plan(new[] { Chrome }, null!));
        }

        [Fact]
        public void Plan_PackagedApp_MatchesByAumid_AndStartsAgainByAumidOnce() {
            Add(new ProcessEntry { Pid = 3000, ParentPid = 800, ImagePath = TerminalExe, Aumid = TerminalAumid, HasVisibleWindow = true });
            Add(new ProcessEntry { Pid = 3001, ParentPid = 801, ImagePath = TerminalExe, Aumid = TerminalAumid, HasVisibleWindow = true });
            RestartEntry e = Only(TerminalAumid);
            Assert.True(e.Packaged);
            Assert.Equal(2, e.TopLevel.Count);
            LaunchSpec l = Assert.Single(e.Launches);
            Assert.Equal(TerminalAumid, l.Aumid);
            Assert.Null(l.CommandLine);
        }

        [Fact]
        public void Plan_TwoInstances_AreTwoLaunches() {
            Add(new ProcessEntry { Pid = 4000, ParentPid = ShellPid, ImagePath = Touch, CommandLine = "\"" + Touch + "\" A.toe", HasVisibleWindow = true });
            Add(new ProcessEntry { Pid = 4001, ParentPid = ShellPid, ImagePath = Touch, CommandLine = "\"" + Touch + "\" B.toe", HasVisibleWindow = true });
            RestartEntry e = Only(Touch);
            Assert.Equal(new[] { "\"" + Touch + "\" A.toe", "\"" + Touch + "\" B.toe" }, e.Launches.Select(l => l.CommandLine));
        }

        [Fact]
        public void Plan_AParentStartedAfterItsChild_IsAnotherProcess_SoTheChildIsTopLevel() {
            // 5001's parent exited; its id 5000 now names a newer process of the same app
            Add(new ProcessEntry { Pid = 5001, ParentPid = 5000, CreationTime = 100, ImagePath = Touch, CommandLine = "\"" + Touch + "\" old.toe" });
            Add(new ProcessEntry { Pid = 5000, ParentPid = ShellPid, CreationTime = 200, ImagePath = Touch, CommandLine = "\"" + Touch + "\" new.toe" });
            RestartEntry e = Only(Touch);
            Assert.Equal(new[] { 5001, 5000 }, e.TopLevel.Select(p => p.Pid));
        }

        [Fact]
        public void Plan_Explorer_IsListed_ButTakesEffectAtTheNextSignIn() {
            RestartEntry e = Only(Explorer);
            Assert.Equal(RestartBlock.Explorer, e.Block);
            Assert.False(e.Restartable);
            Assert.Empty(e.Launches);
            Assert.Contains("next sign-in", RestartText.Reason(e.Block));
        }

        [Fact]
        public void Plan_OmenMonItself_IsNotRestarted() {
            Add(new ProcessEntry { Pid = OwnPid, ParentPid = ShellPid, ImagePath = @"C:\Program Files\OmenMon\OmenMon.exe", CommandLine = "OmenMon.exe", Elevated = false });
            Assert.Equal(RestartBlock.Self, Only(@"C:\Program Files\OmenMon\OmenMon.exe").Block);
        }

        [Fact]
        public void Plan_AnElevatedProcess_MakesTheAppNotRestartable() {
            AddChrome();
            this.catalog.Running.Single(p => p.Pid == 2002).Elevated = true;
            RestartEntry e = Only(Chrome);
            Assert.Equal(RestartBlock.Elevated, e.Block);
            Assert.Empty(e.Launches);
        }

        [Fact]
        public void Plan_ElevationUnknown_IsNotRestartable() {
            AddChrome();
            this.catalog.Running.Single(p => p.Pid == 2001).Elevated = null;
            Assert.Equal(RestartBlock.ElevationUnknown, Only(Chrome).Block);
        }

        [Theory]
        [InlineData("service")]
        [InlineData("other user")]
        [InlineData("user unknown")]
        [InlineData("other session")]
        public void Plan_AnotherUserServiceOrSession_IsNotRestartable(string how) {
            ProcessEntry p = new ProcessEntry { Pid = 6000, ParentPid = 700, ImagePath = Container, CommandLine = "\"" + Container + "\" -s NVDisplay.ContainerLocalSystem" };
            switch(how) {
                case "service": p.SessionId = 0; p.InteractiveUser = false; break;
                case "other user": p.InteractiveUser = false; break;
                case "other session": p.SessionId = 2; break;
            }
            Add(p);
            if(how == "user unknown") p.InteractiveUser = null;
            Assert.Equal(RestartBlock.OtherUser, Only(Container).Block);
        }

        [Fact]
        public void Plan_NoShell_NothingIsRestartable() {
            AddChrome();
            ProcessSnapshot s = this.catalog.Snapshot();
            s.InteractiveSession = -1;
            Assert.Equal(RestartBlock.OtherUser, Assert.Single(RestartPlanner.Plan(new[] { Chrome }, s)).Block);
        }

        [Fact]
        public void Plan_CommandLineUnknown_StartsTheExecutableAlone() {
            Add(new ProcessEntry { Pid = 7000, ParentPid = ShellPid, ImagePath = Touch, CommandLine = null, HasVisibleWindow = true });
            RestartEntry e = Only(Touch);
            Assert.True(e.Restartable);
            Assert.True(e.ArgumentsUnknown);
            Assert.Equal("\"" + Touch + "\"", Assert.Single(e.Launches).CommandLine);
        }

        [Fact]
        public void Plan_NoWayToStartItAgain_IsNotRestartable() {
            // Matched by AUMID, but the AUMID could change the command line
            Add(new ProcessEntry { Pid = 7100, ParentPid = ShellPid, Aumid = "Evil App!x\" calc", HasVisibleWindow = true });
            Assert.Equal(RestartBlock.NoCommandLine, Only("Evil App!x\" calc").Block);
        }

        [Fact]
        public void Plan_ACommandLineTooLongForATokenLaunch_IsNotRestartable() {
            Add(new ProcessEntry { Pid = 7200, ParentPid = ShellPid, ImagePath = Touch, CommandLine = "\"" + Touch + "\" " + new string('x', 1000) });
            Assert.Equal(RestartBlock.CommandLineTooLong, Only(Touch).Block);
        }
#endregion

#region Coordinator
        [Fact]
        public void Restart_ClosesPolitely_StartsAgainUnelevated_AndSeesItRunning() {
            AddChrome();
            RestartCoordinator c = NewCoordinator();
            RestartResult r = Assert.Single(c.Run(c.Plan(new[] { Chrome })));
            Assert.Equal(RestartOutcome.Restarted, r.Outcome);
            Assert.Contains(2000, this.closer.Asked);
            Assert.Empty(this.closer.Ended);
            var start = Assert.Single(this.launcher.Starts);
            Assert.Equal(Chrome, start.Image);
            Assert.Equal("\"" + Chrome + "\" --profile-directory=Default", start.CommandLine);
            Assert.Equal(@"C:\Program Files\Google\Chrome\Application", start.Cwd);
            Assert.Equal(Assert.Single(r.Started).Pid, Assert.Single(r.Launches).Pid);
            Assert.Contains(this.log, l => l.Contains("Restarted"));
        }

        [Fact]
        public void Restart_PackagedApp_IsStartedByAumid() {
            Add(new ProcessEntry { Pid = 3000, ParentPid = 800, ImagePath = TerminalExe, Aumid = TerminalAumid, HasVisibleWindow = true });
            RestartCoordinator c = NewCoordinator();
            RestartResult r = c.Restart(Only(TerminalAumid));
            Assert.Equal(RestartOutcome.Restarted, r.Outcome);
            Assert.Equal(TerminalAumid, Assert.Single(this.launcher.Packaged));
            Assert.Empty(this.launcher.Starts);
        }

        // Safety: an app that does not close is never ended without the user's word,
        // and not started a second time next to itself
        [Fact]
        public void Restart_StillRunningAfterThePoliteClose_NeedsADecision_NothingEndedOrStarted() {
            AddChrome();
            this.closer.Stubborn.Add(2000);
            RestartCoordinator c = NewCoordinator();
            DateTime start = this.now;
            RestartResult r = c.Restart(Only(Chrome));
            Assert.Equal(RestartOutcome.NeedsDecision, r.Outcome);
            Assert.Contains(r.StillRunning, p => p.Pid == 2000);
            Assert.Empty(this.closer.Ended);
            Assert.Empty(this.launcher.Starts);
            Assert.True(this.now - start <= c.CloseTimeout + TimeSpan.FromSeconds(1), "waited " + (this.now - start));
        }

        [Fact]
        public void Restart_NoWindowToClose_NeedsADecision_AtOnce() {
            Add(new ProcessEntry { Pid = 7300, ParentPid = ShellPid, ImagePath = Touch, CommandLine = "\"" + Touch + "\"", HasVisibleWindow = false });
            RestartCoordinator c = NewCoordinator();
            RestartResult r = c.Restart(Only(Touch));
            Assert.Equal(RestartOutcome.NeedsDecision, r.Outcome);
            Assert.Empty(this.closer.Ended);
            Assert.Empty(this.launcher.Starts);
            Assert.True(this.catalog.IsRunning(7300));
        }

        [Fact]
        public void Decide_EndIt_EndsWhatIsStillRunning_ThenStartsItAgain() {
            AddChrome();
            this.closer.Stubborn.Add(2000);
            RestartCoordinator c = NewCoordinator();
            RestartResult pending = c.Restart(Only(Chrome));
            RestartResult r = c.Decide(pending, endIt: true);
            Assert.Equal(RestartOutcome.Restarted, r.Outcome);
            Assert.Equal(new[] { 2000 }, this.closer.Ended);
            Assert.Single(this.launcher.Starts);
        }

        [Fact]
        public void Decide_Skip_EndsNothing_StartsNothing() {
            AddChrome();
            this.closer.Stubborn.Add(2000);
            RestartCoordinator c = NewCoordinator();
            RestartResult r = c.Decide(c.Restart(Only(Chrome)), endIt: false);
            Assert.Equal(RestartOutcome.Skipped, r.Outcome);
            Assert.Empty(this.closer.Ended);
            Assert.Empty(this.launcher.Starts);
        }

        [Fact]
        public void Decide_EndItButItSurvives_IsReported_NotStarted() {
            AddChrome();
            this.closer.Stubborn.Add(2000);
            this.closer.Unkillable.Add(2000);
            RestartCoordinator c = NewCoordinator();
            RestartResult r = c.Decide(c.Restart(Only(Chrome)), endIt: true);
            Assert.Equal(RestartOutcome.EndFailed, r.Outcome);
            Assert.Empty(this.launcher.Starts);
        }

        [Fact]
        public void Decide_OnlyForAPendingDecision() {
            AddChrome();
            RestartCoordinator c = NewCoordinator();
            RestartResult done = c.Restart(Only(Chrome));
            Assert.Same(done, c.Decide(done, endIt: true));
            Assert.Empty(this.closer.Ended);
            Assert.Single(this.launcher.Starts);
        }

        // Safety: what the planner marks not restartable is never touched, whatever the UI passes
        [Theory]
        [InlineData("elevated")]
        [InlineData("explorer")]
        [InlineData("other user")]
        public void Restart_NotRestartable_NothingClosedOrStarted(string how) {
            string key = Chrome;
            if(how == "explorer")
                key = Explorer;
            else {
                AddChrome();
                ProcessEntry p = this.catalog.Running.Single(x => x.Pid == 2000);
                if(how == "elevated") p.Elevated = true; else p.InteractiveUser = false;
            }
            RestartCoordinator c = NewCoordinator();
            RestartEntry forged = new RestartEntry { AppKey = key, Block = RestartBlock.None };
            RestartResult r = c.Restart(forged);
            Assert.Equal(RestartOutcome.NotRestartable, r.Outcome);
            Assert.Empty(this.closer.Asked);
            Assert.Empty(this.closer.Ended);
            Assert.Empty(this.launcher.Starts);
        }

        // An instance started elevated since the list was shown makes the app not restartable
        [Fact]
        public void Restart_LooksTheAppUpAgain_AnElevatedInstanceSinceThePlanBlocksIt() {
            AddChrome();
            RestartEntry planned = Only(Chrome);
            Add(new ProcessEntry { Pid = 2100, ParentPid = ShellPid, ImagePath = Chrome, CommandLine = "\"" + Chrome + "\"", Elevated = true, HasVisibleWindow = true });
            RestartResult r = NewCoordinator().Restart(planned);
            Assert.Equal(RestartOutcome.NotRestartable, r.Outcome);
            Assert.Equal(RestartBlock.Elevated, r.Block);
            Assert.Empty(this.closer.Asked);
        }

        [Fact]
        public void Restart_AppExitedSinceThePlan_NothingToDo() {
            AddChrome();
            RestartEntry planned = Only(Chrome);
            this.catalog.Running.RemoveAll(p => p.ImagePath == Chrome);
            RestartResult r = NewCoordinator().Restart(planned);
            Assert.Equal(RestartOutcome.NotRunning, r.Outcome);
            Assert.Empty(this.launcher.Starts);
        }

        [Fact]
        public void Restart_ProcessesCannotBeListed_NothingDone() {
            AddChrome();
            RestartEntry planned = Only(Chrome);
            this.catalog.Unavailable = true;
            RestartCoordinator c = NewCoordinator();
            Assert.Null(c.Plan(new[] { Chrome }));
            Assert.Equal(RestartOutcome.CatalogUnavailable, c.Restart(planned).Outcome);
            Assert.Empty(this.closer.Asked);
        }

        // Safety: without an unelevated way to start it again, the app is not even closed
        [Theory]
        [InlineData(LaunchStatus.ShellElevated)]
        [InlineData(LaunchStatus.NoShell)]
        [InlineData(LaunchStatus.ShellOtherSession)]
        public void Restart_NoUnelevatedLaunch_RefusedBeforeAnythingIsClosed(LaunchStatus status) {
            AddChrome();
            this.launcher.CheckStatus = status;
            RestartResult r = NewCoordinator().Restart(Only(Chrome));
            Assert.Equal(RestartOutcome.LaunchRefused, r.Outcome);
            Assert.Empty(this.closer.Asked);
            Assert.Empty(this.launcher.Starts);
            Assert.True(this.catalog.IsRunning(2000));
        }

        [Fact]
        public void Decide_EndIt_RefusedBeforeEndingWhenTheShellTurnedElevated() {
            AddChrome();
            this.closer.Stubborn.Add(2000);
            RestartCoordinator c = NewCoordinator();
            RestartResult pending = c.Restart(Only(Chrome));
            this.launcher.CheckStatus = LaunchStatus.ShellElevated;
            Assert.Equal(RestartOutcome.LaunchRefused, c.Decide(pending, endIt: true).Outcome);
            Assert.Empty(this.closer.Ended);
        }

        [Theory]
        [InlineData(LaunchStatus.StartedElevated, RestartOutcome.StartedElevatedEnded)]
        [InlineData(LaunchStatus.Failed, RestartOutcome.LaunchFailed)]
        [InlineData(LaunchStatus.Invalid, RestartOutcome.LaunchFailed)]
        [InlineData(LaunchStatus.ShellElevated, RestartOutcome.LaunchRefused)]
        [InlineData(LaunchStatus.NoShell, RestartOutcome.LaunchRefused)]
        public void Restart_LaunchNotStarted_IsReported(LaunchStatus status, RestartOutcome outcome) {
            AddChrome();
            this.launcher.StartStatus = status;
            RestartResult r = NewCoordinator().Restart(Only(Chrome));
            Assert.Equal(outcome, r.Outcome);
            Assert.Equal(status, Assert.Single(r.Launches).Status);
        }

        // Safety: a new process of the app that runs elevated anyway is ended at once
        [Fact]
        public void Restart_NewProcessRunsElevated_IsEnded() {
            AddChrome();
            this.launcher.NewElevated = true;
            RestartResult r = NewCoordinator().Restart(Only(Chrome));
            Assert.Equal(RestartOutcome.StartedElevatedEnded, r.Outcome);
            int started = Assert.Single(r.Launches).Pid;
            Assert.Equal(new[] { started }, this.closer.Ended);
            Assert.False(this.catalog.IsRunning(started));
        }

        [Fact]
        public void Restart_NewProcessElevationUnknown_IsUnverified() {
            AddChrome();
            this.launcher.NewElevated = null;
            RestartResult r = NewCoordinator().Restart(Only(Chrome));
            Assert.Equal(RestartOutcome.Unverified, r.Outcome);
            Assert.Empty(this.closer.Ended);
        }

        [Fact]
        public void Restart_StartedButNotSeen_IsReported_AfterTheVerifyTime() {
            AddChrome();
            this.launcher.AppAppears = false;
            RestartCoordinator c = NewCoordinator();
            DateTime start = this.now;
            RestartResult r = c.Restart(Only(Chrome));
            Assert.Equal(RestartOutcome.NotSeenRunning, r.Outcome);
            Assert.True(this.now - start >= c.VerifyTimeout);
        }

#endregion

#region Launch policy
        [Fact]
        public void CheckShell_OnlyAKnownUnelevatedShellInOurSession_IsReady() {
            Assert.Equal(LaunchStatus.Ready, LaunchPolicy.CheckShell(true, 1, 1, false));
            Assert.Equal(LaunchStatus.NoShell, LaunchPolicy.CheckShell(false, 1, 1, false));
            Assert.Equal(LaunchStatus.ShellElevated, LaunchPolicy.CheckShell(true, 1, 1, true));
            Assert.Equal(LaunchStatus.ShellElevated, LaunchPolicy.CheckShell(true, 1, 1, null));
            Assert.Equal(LaunchStatus.ShellOtherSession, LaunchPolicy.CheckShell(true, 2, 1, false));
            Assert.Equal(LaunchStatus.ShellOtherSession, LaunchPolicy.CheckShell(true, -1, 1, false));
        }

        [Fact]
        public void MustEnd_UnlessKnownUnelevated() {
            Assert.True(LaunchPolicy.MustEnd(true));
            Assert.True(LaunchPolicy.MustEnd(null));
            Assert.False(LaunchPolicy.MustEnd(false));
        }

        [Theory]
        [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", true)]
        [InlineData("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", true)]
        [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe", false)]
        [InlineData("a!b!c", false)]
        [InlineData("!App", false)]
        [InlineData("Pkg_x!App calc.exe", false)]
        [InlineData("Pkg_x!App\"", false)]
        [InlineData(@"Pkg_x!..\..\x", false)]
        [InlineData("", false)]
        public void IsValidAumid(string aumid, bool valid) {
            Assert.Equal(valid, LaunchPolicy.IsValidAumid(aumid));
        }

        [Fact]
        public void CheckStart_AbsoluteExecutableAndACommandLineThatFits() {
            Assert.Equal(LaunchStatus.Ready, LaunchPolicy.CheckStart(Touch, "\"" + Touch + "\""));
            Assert.Equal(LaunchStatus.Invalid, LaunchPolicy.CheckStart("TouchDesigner.exe", "x"));
            Assert.Equal(LaunchStatus.Invalid, LaunchPolicy.CheckStart("C:\\a\"b.exe", "x"));
            Assert.Equal(LaunchStatus.Invalid, LaunchPolicy.CheckStart(Touch, new string('x', 1024)));
            Assert.Equal(LaunchStatus.Ready, LaunchPolicy.CheckStart(Touch, new string('x', 1023)));
        }

        [Fact]
        public void PackagedCommandLine_StartsExplorerWithTheAppsFolder() {
            Assert.Equal("\"C:\\Windows\\explorer.exe\" shell:AppsFolder\\" + TerminalAumid, LaunchPolicy.PackagedCommandLine(Explorer, TerminalAumid));
        }
#endregion

#region Launcher (Win32 calls faked)
        [Fact]
        public void Launcher_UnelevatedShell_StartsSuspended_ChecksTheNewToken_ThenResumes() {
            FakeLaunchApi api = new FakeLaunchApi();
            LaunchResult r = new UnelevatedLauncher(api).Start(Touch, "\"" + Touch + "\" show.toe", @"C:\Shows");
            Assert.Equal(LaunchStatus.Started, r.Status);
            Assert.Equal(777, r.Pid);
            Assert.Equal((1, 1, 0), (api.Created, api.Resumed, api.Terminated));
            Assert.Equal(Touch, api.Image);
            Assert.Equal("\"" + Touch + "\" show.toe", api.CommandLine);
            Assert.Empty(api.Open);
        }

        // Safety: an elevated shell token is never used
        [Theory]
        [InlineData(true)]
        [InlineData(null)]
        public void Launcher_ShellElevatedOrUnknown_RefusesBeforeStarting(bool? elevated) {
            FakeLaunchApi api = new FakeLaunchApi { ShellElevated = elevated };
            UnelevatedLauncher l = new UnelevatedLauncher(api);
            Assert.Equal(LaunchStatus.ShellElevated, l.Check().Status);
            Assert.Equal(LaunchStatus.ShellElevated, l.Start(Touch, null, null).Status);
            Assert.Equal(LaunchStatus.ShellElevated, l.StartPackaged(TerminalAumid).Status);
            Assert.Equal(0, api.Created);
            Assert.Equal(0, api.Duplicated);
            Assert.Empty(api.Open);
        }

        [Fact]
        public void Launcher_NoShellWindow_RefusesBeforeStarting() {
            FakeLaunchApi api = new FakeLaunchApi { ShellFound = false };
            UnelevatedLauncher l = new UnelevatedLauncher(api);
            Assert.Equal(LaunchStatus.NoShell, l.Check().Status);
            Assert.Equal(LaunchStatus.NoShell, l.Start(Touch, null, null).Status);
            Assert.Equal(0, api.Created);
        }

        [Fact]
        public void Launcher_ShellInAnotherSession_RefusesBeforeStarting() {
            FakeLaunchApi api = new FakeLaunchApi { ShellSession = 2 };
            Assert.Equal(LaunchStatus.ShellOtherSession, new UnelevatedLauncher(api).Start(Touch, null, null).Status);
            Assert.Equal(0, api.Created);
        }

        [Fact]
        public void Launcher_TokenCopyElevated_RefusesBeforeStarting() {
            FakeLaunchApi api = new FakeLaunchApi { CopyElevated = null };
            Assert.Equal(LaunchStatus.ShellElevated, new UnelevatedLauncher(api).Start(Touch, null, null).Status);
            Assert.Equal(0, api.Created);
            Assert.Empty(api.Open);
        }

        // Safety: a new process that is elevated (or cannot be checked) never runs
        [Theory]
        [InlineData(true)]
        [InlineData(null)]
        public void Launcher_NewProcessElevatedOrUnknown_IsTerminated_NeverResumed(bool? elevated) {
            FakeLaunchApi api = new FakeLaunchApi { ProcessElevated = elevated };
            LaunchResult r = new UnelevatedLauncher(api).Start(Touch, null, null);
            Assert.Equal(LaunchStatus.StartedElevated, r.Status);
            Assert.Equal((1, 0, 1), (api.Created, api.Resumed, api.Terminated));
            Assert.Empty(api.Open);
        }

        [Fact]
        public void Launcher_NotResumed_IsTerminated() {
            FakeLaunchApi api = new FakeLaunchApi { ResumeOk = false };
            Assert.Equal(LaunchStatus.Failed, new UnelevatedLauncher(api).Start(Touch, null, null).Status);
            Assert.Equal(1, api.Terminated);
        }

        [Fact]
        public void Launcher_CreateFails_ReportsTheError() {
            FakeLaunchApi api = new FakeLaunchApi { CreateOk = false };
            LaunchResult r = new UnelevatedLauncher(api).Start(Touch, null, null);
            Assert.Equal(LaunchStatus.Failed, r.Status);
            Assert.Equal(740, r.Error);
            Assert.Empty(api.Open);
        }

        [Fact]
        public void Launcher_PackagedApp_StartsExplorerWithTheAppsFolder() {
            FakeLaunchApi api = new FakeLaunchApi();
            Assert.Equal(LaunchStatus.Started, new UnelevatedLauncher(api).StartPackaged(TerminalAumid).Status);
            Assert.Equal(Explorer, api.Image);
            Assert.Equal("\"" + Explorer + "\" shell:AppsFolder\\" + TerminalAumid, api.CommandLine);
        }

        [Fact]
        public void Launcher_BadInput_StartsNothing() {
            FakeLaunchApi api = new FakeLaunchApi();
            UnelevatedLauncher l = new UnelevatedLauncher(api);
            Assert.Equal(LaunchStatus.Invalid, l.Start("notepad.exe", null, null).Status);
            Assert.Equal(LaunchStatus.Invalid, l.StartPackaged("x!y \"calc\"").Status);
            Assert.Equal(0, api.Created);
        }

        [Fact]
        public void Launcher_NoCommandLine_StartsTheQuotedPath() {
            FakeLaunchApi api = new FakeLaunchApi();
            new UnelevatedLauncher(api).Start(Touch, null, null);
            Assert.Equal("\"" + Touch + "\"", api.CommandLine);
        }
#endregion

#region Source tripwire
        // The Graphics apps code starts processes in one way only: CreateProcessWithTokenW
        // with the shell's token. A Process.Start or ShellExecute from the elevated
        // OmenMon would start the app elevated.
        [Theory]
        [InlineData("GraphicsRestart.cs")]
        [InlineData("GraphicsRestartWindows.cs")]
        public void NoOtherWayToStartAProcess(string file) {
            DirectoryInfo? d = new DirectoryInfo(AppContext.BaseDirectory);
            while(d != null && !File.Exists(Path.Combine(d.FullName, "OmenMon.xml")))
                d = d.Parent;
            Assert.NotNull(d);
            string source = File.ReadAllText(Path.Combine(d!.FullName, "Library", file));
            // Comments may name the forbidden calls
            string code = Regex.Replace(source, @"//[^\n]*", "");
            Assert.DoesNotMatch(new Regex(@"Process\s*\.\s*Start\b|new\s+ProcessStartInfo|ShellExecute|WinExec|\bCreateProcess(A|W|AsUser\w*)?\s*\("), code);
            if(file == "GraphicsRestartWindows.cs")
                Assert.Matches(new Regex(@"AdvApi32\.CreateProcessWithTokenW\("), code);
        }
#endregion

    }

}
