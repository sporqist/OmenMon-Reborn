  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace OmenMon.Library {

    // Graphics apps, process side: restarting the running apps whose GPU choice changed.
    // An app reads its GPU preference when it starts, so a running app takes a new one
    // only once it has been started again.
    //
    //  * The restart list comes first (RestartPlanner): the running processes of each
    //    app, grouped (Chrome's many processes are one entry), with what is needed to
    //    start it again, and whether it can be restarted at all and why not. The UI
    //    shows it; nothing is closed before the user confirms.
    //  * Closing is polite only: WM_CLOSE to the app's top-level windows, then waiting.
    //    An app still running after that is a decision for the user (end it / skip):
    //    it is never ended without one.
    //  * Elevated apps, apps of another user or session (services too), OmenMon itself
    //    and Explorer are listed but never closed or started.
    //  * A restarted app never runs elevated (Marius: "under no circumstance"): it is
    //    started with the token of the shell (Explorer) and only if that is not elevated,
    //    the new process is checked before it runs, and any new process of the app that
    //    runs elevated anyway is ended. There is no other way to start it.
    //
    // This file holds the decisions, testable with fakes; the Win32 side is in
    // GraphicsRestartWindows.cs and kept thin.

#region Model
    // A running process, as the catalog sees it
    public sealed class ProcessEntry {
        public int Pid;
        public int ParentPid;
        public long CreationTime;       // FILETIME; with the pid it names this process (0 = unknown)
        public string ImagePath;        // full executable path (QueryFullProcessImageName), null if unreadable
        public string Aumid;            // Application User Model ID of a packaged app, else null
        public int SessionId = -1;      // -1 = unknown
        public bool? Elevated;          // token elevation; null = could not be read
        public bool? InteractiveUser;   // runs as the user the shell runs as; null = could not be read
        public bool HasVisibleWindow;   // owns a visible top-level window
        public string CommandLine;      // null if unknown
        public string WorkingDirectory; // null if unknown (not readable reliably: the image directory stands in)

        public override string ToString() {
            return (ImagePath != null ? Path.GetFileName(ImagePath) : Aumid ?? "?") + " (" + Pid + ")";
        }
    }

    // All the running processes at one moment
    public sealed class ProcessSnapshot {
        public List<ProcessEntry> Processes = new List<ProcessEntry>();
        public int OwnPid;                  // OmenMon itself
        public int InteractiveSession = -1; // the shell's session; -1 = no shell
    }

    // Why an app cannot be restarted
    public enum RestartBlock {
        None,               // it can
        Explorer,           // explorer.exe: takes effect at the next sign-in
        Self,               // OmenMon itself: takes effect at its next start
        NoShell,            // the shell (Explorer) is not running: nothing can be started unelevated
        OtherUser,          // another user, a service or another session (or not readable)
        Elevated,           // runs elevated: it would come back unelevated, so it is left alone
        ElevationUnknown,   // whether it runs elevated could not be read
        NoCommandLine,      // how to start it again is not known
        CommandLineTooLong  // longer than a token launch takes (1024 characters)
    }

    // How to start one top-level process again: a packaged app by its AUMID, else its
    // executable with the original command line in a working directory
    public sealed class LaunchSpec {
        public string Aumid;
        public string ImagePath;
        public string CommandLine;
        public string WorkingDirectory;
        public bool ArgumentsUnknown;   // the command line was not readable: the executable alone
    }

    // One app on the restart list
    public sealed class RestartEntry {
        public string AppKey;                                       // as the profile names it
        public List<ProcessEntry> Processes = new List<ProcessEntry>();
        public List<ProcessEntry> TopLevel = new List<ProcessEntry>(); // started from outside the app
        public List<LaunchSpec> Launches = new List<LaunchSpec>();
        public bool Packaged;
        public bool HasVisibleWindow;   // false: it can only be ended, not closed politely
        public bool ArgumentsUnknown;
        public RestartBlock Block;

        public bool Restartable { get { return this.Block == RestartBlock.None; } }

        public string DisplayName {
            get {
                ProcessEntry p = this.TopLevel.FirstOrDefault() ?? this.Processes.FirstOrDefault();
                if(p?.ImagePath != null)
                    return Path.GetFileName(p.ImagePath);
                return p?.Aumid ?? this.AppKey;
            }
        }
    }
#endregion

#region Seams
    // The running processes
    public interface IProcessCatalog {
        // Null if they cannot be listed
        ProcessSnapshot Snapshot();
    }

    // Closing processes. Every call checks the process is still the one in the entry
    // (pid and creation time) and does nothing to another one.
    public interface IProcessCloser {
        // Posts WM_CLOSE to the process's visible top-level windows; false if it has none
        bool RequestClose(ProcessEntry p);
        // Whether the process has exited, waiting up to the timeout
        bool WaitForExit(ProcessEntry p, TimeSpan timeout);
        // Ends the process (TerminateProcess). Only after the user said so, or for a
        // process OmenMon itself just started that runs elevated.
        bool EndProcess(ProcessEntry p);
    }

    // Starting apps unelevated, as the interactive user
    public interface IUnelevatedLauncher {
        // Ready if an app could be started now (the shell is there and not elevated)
        LaunchResult Check();
        // An executable with its command line (null: the quoted path) and working directory
        LaunchResult Start(string imagePath, string commandLine, string workingDirectory);
        // A packaged app, through explorer.exe shell:AppsFolder\<AUMID>
        LaunchResult StartPackaged(string aumid);
    }

    public enum LaunchStatus {
        Ready,              // Check(): an app can be started
        Started,
        NoShell,            // no shell window, or its process or token cannot be opened
        ShellOtherSession,  // the shell runs in another session than OmenMon
        ShellElevated,      // the shell's token is elevated (or that cannot be read): refused
        Invalid,            // not an absolute path, a malformed AUMID, or a command line too long
        Failed,             // the system refused to start it
        StartedElevated     // it was elevated (or could not be checked) and was ended before it ran
    }

    public sealed class LaunchResult {
        public LaunchStatus Status;
        public int Pid;         // Started, StartedElevated
        public int Error;       // Win32 error, Failed
        public string Detail;

        public static LaunchResult Of(LaunchStatus status, string detail = null, int pid = 0, int error = 0) {
            return new LaunchResult { Status = status, Detail = detail, Pid = pid, Error = error };
        }

        public override string ToString() {
            return Status + (Pid != 0 ? " pid " + Pid : "") + (Error != 0 ? " error " + Error : "")
                + (Detail != null ? ": " + Detail : "");
        }
    }
#endregion

#region Launch policy
    // The launcher's decisions, apart from the Win32 calls
    public static class LaunchPolicy {

        // CreateProcessWithTokenW takes at most 1024 characters, the terminating zero included
        public const int MaxCommandLine = 1023;

        // Whether the shell's token may be used to start an app. Anything but a token
        // known to be unelevated, of the shell in OmenMon's own session, is refused.
        public static LaunchStatus CheckShell(bool shellFound, int shellSession, int ownSession, bool? shellElevated) {
            if(!shellFound)
                return LaunchStatus.NoShell;
            if(shellSession < 0 || shellSession != ownSession)
                return LaunchStatus.ShellOtherSession;
            if(shellElevated != false)
                return LaunchStatus.ShellElevated;
            return LaunchStatus.Ready;
        }

        // Whether a process just started (still suspended) is ended instead of resumed:
        // unless its token is known to be unelevated
        public static bool MustEnd(bool? elevated) {
            return elevated != false;
        }

        // An executable launch: an absolute path to an .exe, a command line that fits
        public static LaunchStatus CheckStart(string imagePath, string commandLine) {
            if(string.IsNullOrEmpty(imagePath) || imagePath.IndexOf('"') >= 0 || !IsAbsolute(imagePath))
                return LaunchStatus.Invalid;
            if(string.IsNullOrEmpty(commandLine) || commandLine.Length > MaxCommandLine)
                return LaunchStatus.Invalid;
            return LaunchStatus.Ready;
        }

        // PackageFamilyName!AppId: letters, digits, '.', '_', '-' and exactly one '!'.
        // Anything else (spaces, quotes, backslashes) could change the command line.
        public static bool IsValidAumid(string aumid) {
            if(string.IsNullOrEmpty(aumid) || aumid.Length > 512)
                return false;
            int bang = 0;
            foreach(char c in aumid) {
                if(c == '!')
                    bang++;
                else if(!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '.' || c == '_' || c == '-'))
                    return false;
            }
            return bang == 1 && aumid[0] != '!' && aumid[aumid.Length - 1] != '!';
        }

        public static string Quote(string path) {
            return "\"" + path + "\"";
        }

        public static string PackagedCommandLine(string explorerPath, string aumid) {
            return Quote(explorerPath) + @" shell:AppsFolder\" + aumid;
        }

        // A drive-letter or UNC path; no exceptions on odd characters
        public static bool IsAbsolute(string path) {
            if(string.IsNullOrEmpty(path))
                return false;
            if(path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\')
                return true;
            return path.StartsWith(@"\\", StringComparison.Ordinal);
        }

        // The directory of an executable, or null
        public static string DirectoryOf(string path) {
            try {
                return Path.GetDirectoryName(path);
            } catch {
                return null;
            }
        }

    }
#endregion

#region Planner
    // The restart list (pure)
    public static class RestartPlanner {

        // Running processes of the given apps, one entry per app, in the order given;
        // apps that are not running are left out. A process goes to the first app it matches.
        public static List<RestartEntry> Plan(IEnumerable<string> appKeys, ProcessSnapshot snapshot) {
            List<RestartEntry> entries = new List<RestartEntry>();
            if(appKeys == null || snapshot == null)
                return entries;
            HashSet<int> taken = new HashSet<int>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string appKey in appKeys) {
                string key = NormalizeKey(appKey);
                if(key == null || !seen.Add(key))
                    continue;
                List<ProcessEntry> group = snapshot.Processes.Where(p => !taken.Contains(p.Pid) && Matches(key, p)).ToList();
                if(group.Count == 0)
                    continue;
                foreach(ProcessEntry p in group)
                    taken.Add(p.Pid);
                entries.Add(Entry(appKey, group, snapshot));
            }
            return entries;
        }

        // App keys compare case-insensitively, with either slash, without a \\?\ prefix
        public static string NormalizeKey(string key) {
            if(string.IsNullOrWhiteSpace(key))
                return null;
            string k = key.Trim().Replace('/', '\\');
            if(k.StartsWith(@"\\?\", StringComparison.Ordinal))
                k = k.Substring(4);
            return k;
        }

        // Whether a process is an instance of the app: by AUMID or by executable path
        public static bool Matches(string normalizedKey, ProcessEntry p) {
            if(normalizedKey == null || p == null)
                return false;
            if(p.Aumid != null && string.Equals(p.Aumid, normalizedKey, StringComparison.OrdinalIgnoreCase))
                return true;
            return p.ImagePath != null && string.Equals(NormalizeKey(p.ImagePath), normalizedKey, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsExplorer(ProcessEntry p) {
            return p.ImagePath != null
                && string.Equals(FileName(p.ImagePath), "explorer.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static string FileName(string path) {
            int i = path.LastIndexOfAny(new[] { '\\', '/' });
            return i >= 0 ? path.Substring(i + 1) : path;
        }

        // A process started from outside the app (its parent is not one of the app's
        // processes). A parent created after its child is another process that got the
        // parent's id, so the child counts as started from outside.
        public static bool IsTopLevel(ProcessEntry p, IDictionary<int, ProcessEntry> group) {
            if(p.ParentPid == p.Pid || !group.TryGetValue(p.ParentPid, out ProcessEntry parent))
                return true;
            return parent.CreationTime != 0 && p.CreationTime != 0 && parent.CreationTime > p.CreationTime;
        }

        private static RestartEntry Entry(string appKey, List<ProcessEntry> group, ProcessSnapshot snapshot) {
            Dictionary<int, ProcessEntry> byPid = new Dictionary<int, ProcessEntry>();
            foreach(ProcessEntry p in group)
                byPid[p.Pid] = p;
            RestartEntry e = new RestartEntry { AppKey = appKey, Processes = group };
            e.TopLevel = group.Where(p => IsTopLevel(p, byPid)).ToList();
            e.Packaged = e.TopLevel.Any(p => p.Aumid != null);
            e.HasVisibleWindow = group.Any(p => p.HasVisibleWindow);
            e.Block = Block(group, snapshot);
            if(e.Block == RestartBlock.None)
                e.Block = Launches(e.TopLevel, e.Launches);
            if(e.Block != RestartBlock.None)
                e.Launches.Clear();
            e.ArgumentsUnknown = e.Launches.Any(l => l.ArgumentsUnknown);
            return e;
        }

        // The first reason, in this order, that holds for any of the app's processes
        private static RestartBlock Block(List<ProcessEntry> group, ProcessSnapshot snapshot) {
            if(group.Any(IsExplorer))
                return RestartBlock.Explorer;
            if(group.Any(p => p.Pid == snapshot.OwnPid))
                return RestartBlock.Self;
            if(snapshot.InteractiveSession < 0)
                return RestartBlock.NoShell;
            if(group.Any(p => p.InteractiveUser != true || p.SessionId != snapshot.InteractiveSession))
                return RestartBlock.OtherUser;
            if(group.Any(p => p.Elevated == true))
                return RestartBlock.Elevated;
            if(group.Any(p => p.Elevated == null))
                return RestartBlock.ElevationUnknown;
            return RestartBlock.None;
        }

        // One launch per top-level process; a packaged app once per AUMID (the shell
        // activates the app, and a second activation would only open another window)
        private static RestartBlock Launches(List<ProcessEntry> topLevel, List<LaunchSpec> launches) {
            if(topLevel.Count == 0)
                return RestartBlock.NoCommandLine;
            HashSet<string> aumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(ProcessEntry p in topLevel) {
                if(p.Aumid != null) {
                    if(!LaunchPolicy.IsValidAumid(p.Aumid))
                        return RestartBlock.NoCommandLine;
                    if(aumids.Add(p.Aumid))
                        launches.Add(new LaunchSpec { Aumid = p.Aumid });
                    continue;
                }
                if(!LaunchPolicy.IsAbsolute(p.ImagePath) || p.ImagePath.IndexOf('"') >= 0)
                    return RestartBlock.NoCommandLine;
                bool unknown = string.IsNullOrWhiteSpace(p.CommandLine);
                string commandLine = unknown ? LaunchPolicy.Quote(p.ImagePath) : p.CommandLine;
                if(commandLine.Length > LaunchPolicy.MaxCommandLine)
                    return RestartBlock.CommandLineTooLong;
                launches.Add(new LaunchSpec {
                    ImagePath = p.ImagePath,
                    CommandLine = commandLine,
                    WorkingDirectory = !string.IsNullOrEmpty(p.WorkingDirectory) ? p.WorkingDirectory : LaunchPolicy.DirectoryOf(p.ImagePath),
                    ArgumentsUnknown = unknown });
            }
            return RestartBlock.None;
        }

    }
#endregion

#region Coordinator
    public enum RestartOutcome {
        Restarted,              // closed, started again unelevated, seen running
        NotRunning,             // not running any more: it takes the new mode at its next start
        NotRestartable,         // see Block; nothing was done
        CatalogUnavailable,     // the running processes could not be listed; nothing was done
        LaunchRefused,          // no unelevated way to start it (see Launches / Detail); nothing was closed
        NeedsDecision,          // still running after the polite close: end it or skip (Decide)
        Skipped,                // the user chose to leave it running
        EndFailed,              // the user chose to end it, and it is still running
        LaunchFailed,           // closed, but it could not be started again
        StartedElevatedEnded,   // closed; the new process would have run elevated and was ended
        ElevatedCopyLeft,       // closed and started unelevated; an elevated copy appeared as well (through a
                                // UAC prompt the user answered, not from OmenMon): reported, left running
        NotSeenRunning,         // closed and started, but not seen running within the verify time
        Unverified              // closed and started; whether the new process is elevated could not be read
    }

    public sealed class RestartResult {
        public string AppKey;
        public RestartOutcome Outcome;
        public RestartBlock Block;              // NotRestartable: why
        public RestartEntry Entry;              // the app as found when the restart began
        public List<ProcessEntry> StillRunning = new List<ProcessEntry>();   // NeedsDecision, Skipped, EndFailed
        public List<LaunchResult> Launches = new List<LaunchResult>();
        public List<ProcessEntry> Started = new List<ProcessEntry>();        // the app's new processes, as seen
        public string Detail;
    }

    // Runs the restarts the user confirmed. Blocking (closing waits up to CloseTimeout
    // per app): call it off the UI thread.
    public sealed class RestartCoordinator {

        public TimeSpan CloseTimeout = TimeSpan.FromSeconds(15);
        public TimeSpan EndTimeout = TimeSpan.FromSeconds(5);
        public TimeSpan VerifyTimeout = TimeSpan.FromSeconds(10);
        public TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

        private readonly IProcessCatalog catalog;
        private readonly IProcessCloser closer;
        private readonly IUnelevatedLauncher launcher;
        private readonly Action<TimeSpan> sleep;
        private readonly Func<DateTime> clock;
        private readonly Action<string> log;

        public RestartCoordinator(IProcessCatalog catalog, IProcessCloser closer, IUnelevatedLauncher launcher,
            Action<TimeSpan> sleep = null, Func<DateTime> clock = null, Action<string> log = null) {
            this.catalog = catalog;
            this.closer = closer;
            this.launcher = launcher;
            this.sleep = sleep ?? (t => Thread.Sleep(t));
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.log = log;
        }

        // The restart list for these apps (whose GPU choice changed); null if the
        // running processes cannot be listed
        public List<RestartEntry> Plan(IEnumerable<string> appKeys) {
            ProcessSnapshot s = Snapshot();
            return s == null ? null : RestartPlanner.Plan(appKeys, s);
        }

        // The entries the user kept checked, one after the other
        public List<RestartResult> Run(IEnumerable<RestartEntry> confirmed) {
            List<RestartResult> results = new List<RestartResult>();
            if(confirmed != null)
                foreach(RestartEntry e in confirmed)
                    results.Add(Restart(e));
            return results;
        }

        // Restarts one app. The entry is looked up again in the processes running now:
        // it may have exited, gained processes, or become not restartable since the list
        // was shown.
        public RestartResult Restart(RestartEntry planned) {
            string key = planned?.AppKey;
            ProcessSnapshot s = Snapshot();
            if(s == null)
                return Done(new RestartResult { AppKey = key, Outcome = RestartOutcome.CatalogUnavailable, Entry = planned });
            RestartEntry e = RestartPlanner.Plan(new[] { key }, s).FirstOrDefault();
            if(e == null)
                return Done(new RestartResult { AppKey = key, Outcome = RestartOutcome.NotRunning });
            if(!e.Restartable)
                return Done(new RestartResult { AppKey = key, Outcome = RestartOutcome.NotRestartable, Block = e.Block, Entry = e });

            // Before anything is closed: can it be started again?
            LaunchResult ready = this.launcher.Check();
            if(ready == null || ready.Status != LaunchStatus.Ready)
                return Done(Refused(key, e, ready));

            // Polite close only
            int asked = 0;
            foreach(ProcessEntry p in e.Processes)
                if(this.closer.RequestClose(p))
                    asked++;
            if(asked == 0)
                return Done(new RestartResult { AppKey = key, Outcome = RestartOutcome.NeedsDecision, Entry = e,
                    StillRunning = new List<ProcessEntry>(e.Processes), Detail = "it has no window to close" });
            List<ProcessEntry> still = WaitAll(e.Processes, CloseTimeout);
            if(still.Count > 0)
                return Done(new RestartResult { AppKey = key, Outcome = RestartOutcome.NeedsDecision, Entry = e,
                    StillRunning = still, Detail = "still running after " + (int) CloseTimeout.TotalSeconds + " s" });
            return Done(Relaunch(e));
        }

        // The user's answer for an app that is still running: end it (and start it
        // again) or leave it as it is
        public RestartResult Decide(RestartResult pending, bool endIt) {
            if(pending == null || pending.Outcome != RestartOutcome.NeedsDecision || pending.Entry == null)
                return pending;
            RestartEntry e = pending.Entry;
            if(!endIt)
                return Done(new RestartResult { AppKey = e.AppKey, Outcome = RestartOutcome.Skipped, Entry = e,
                    StillRunning = pending.StillRunning });
            if(!e.Restartable)
                return Done(new RestartResult { AppKey = e.AppKey, Outcome = RestartOutcome.NotRestartable, Block = e.Block, Entry = e });
            LaunchResult ready = this.launcher.Check();
            if(ready == null || ready.Status != LaunchStatus.Ready)
                return Done(Refused(e.AppKey, e, ready));
            foreach(ProcessEntry p in pending.StillRunning)
                this.closer.EndProcess(p);
            List<ProcessEntry> still = WaitAll(pending.StillRunning, EndTimeout);
            if(still.Count > 0)
                return Done(new RestartResult { AppKey = e.AppKey, Outcome = RestartOutcome.EndFailed, Entry = e, StillRunning = still });
            return Done(Relaunch(e));
        }

        private static RestartResult Refused(string key, RestartEntry e, LaunchResult ready) {
            RestartResult r = new RestartResult { AppKey = key, Outcome = RestartOutcome.LaunchRefused, Entry = e };
            if(ready != null)
                r.Launches.Add(ready);
            r.Detail = ready != null ? RestartText.Launch(ready.Status) : "no launcher";
            return r;
        }

        private ProcessSnapshot Snapshot() {
            try {
                return this.catalog.Snapshot();
            } catch {
                return null;
            }
        }

        // The processes still running after waiting up to the timeout for all of them
        private List<ProcessEntry> WaitAll(IEnumerable<ProcessEntry> processes, TimeSpan timeout) {
            DateTime deadline = this.clock() + timeout;
            List<ProcessEntry> still = new List<ProcessEntry>();
            foreach(ProcessEntry p in processes) {
                TimeSpan left = deadline - this.clock();
                if(left < TimeSpan.Zero)
                    left = TimeSpan.Zero;
                if(!this.closer.WaitForExit(p, left))
                    still.Add(p);
            }
            return still;
        }

        private static string Id(ProcessEntry p) {
            return p.Pid + ":" + p.CreationTime;
        }

        // Starts the app again (it has exited) and checks its new processes
        private RestartResult Relaunch(RestartEntry e) {
            RestartResult r = new RestartResult { AppKey = e.AppKey, Entry = e };

            // What ran before the launch is not the app's new process
            HashSet<string> before = new HashSet<string>(e.Processes.Select(Id));
            ProcessSnapshot pre = Snapshot();
            if(pre != null)
                foreach(ProcessEntry p in pre.Processes)
                    before.Add(Id(p));

            foreach(LaunchSpec l in e.Launches) {
                LaunchResult lr = l.Aumid != null
                    ? this.launcher.StartPackaged(l.Aumid)
                    : this.launcher.Start(l.ImagePath, l.CommandLine, l.WorkingDirectory);
                lr = lr ?? LaunchResult.Of(LaunchStatus.Failed, "no result");
                r.Launches.Add(lr);
                if(lr.Status != LaunchStatus.Started) {
                    r.Outcome = lr.Status == LaunchStatus.StartedElevated ? RestartOutcome.StartedElevatedEnded
                        : lr.Status == LaunchStatus.Invalid || lr.Status == LaunchStatus.Failed ? RestartOutcome.LaunchFailed
                        : RestartOutcome.LaunchRefused;
                    r.Detail = RestartText.Launch(lr.Status);
                    return r;
                }
            }
            return Verify(e, r, before);
        }

        // Waits for a new process of the app. The process OmenMon started was checked
        // unelevated before it ran (UnelevatedLauncher), so a new one that runs elevated
        // got that from a UAC prompt the user answered: the app elevating itself, or
        // the user starting it as administrator. It is reported, never ended (Marius,
        // 2026-10-21: report only).
        private RestartResult Verify(RestartEntry e, RestartResult r, HashSet<string> before) {
            string key = RestartPlanner.NormalizeKey(e.AppKey);
            DateTime deadline = this.clock() + VerifyTimeout;
            while(true) {
                this.sleep(PollInterval);
                ProcessSnapshot now = Snapshot();
                List<ProcessEntry> fresh = now == null ? new List<ProcessEntry>()
                    : now.Processes.Where(p => !before.Contains(Id(p)) && RestartPlanner.Matches(key, p)).ToList();
                if(fresh.Count > 0) {
                    r.Started = fresh;
                    List<ProcessEntry> elevated = fresh.Where(p => p.Elevated == true).ToList();
                    if(elevated.Count > 0) {
                        r.Outcome = RestartOutcome.ElevatedCopyLeft;
                        r.Detail = string.Join(", ", elevated.Select(p => p.ToString()))
                            + " runs as administrator: started through a UAC prompt, not by OmenMon; left running";
                    } else if(fresh.Any(p => p.Elevated == null)) {
                        r.Outcome = RestartOutcome.Unverified;
                    } else
                        r.Outcome = RestartOutcome.Restarted;
                    return r;
                }
                if(this.clock() >= deadline) {
                    r.Outcome = RestartOutcome.NotSeenRunning;
                    return r;
                }
            }
        }

        private RestartResult Done(RestartResult r) {
            try {
                this.log?.Invoke(RestartText.Describe(r));
            } catch { }
            return r;
        }

    }
#endregion

#region Text
    // Readable forms, for the log and the restart dialog
    public static class RestartText {

        public static string Reason(RestartBlock block) {
            switch(block) {
                case RestartBlock.None: return "can be restarted";
                case RestartBlock.Explorer: return "takes effect at the next sign-in";
                case RestartBlock.Self: return "OmenMon itself: takes effect at its next start";
                case RestartBlock.NoShell: return "the Windows shell (Explorer) is not running";
                case RestartBlock.OtherUser: return "runs as another user, as a service or in another session";
                case RestartBlock.Elevated: return "runs as administrator: not restarted";
                case RestartBlock.ElevationUnknown: return "could not check whether it runs as administrator";
                case RestartBlock.NoCommandLine: return "how to start it again is not known";
                case RestartBlock.CommandLineTooLong: return "its command line is too long to start it again";
                default: return block.ToString();
            }
        }

        public static string Launch(LaunchStatus status) {
            switch(status) {
                case LaunchStatus.Ready: return "ready";
                case LaunchStatus.Started: return "started";
                case LaunchStatus.NoShell: return "the Windows shell (Explorer) is not running";
                case LaunchStatus.ShellOtherSession: return "the Windows shell runs in another session";
                case LaunchStatus.ShellElevated: return "the Windows shell runs as administrator: an app started with it would be too";
                case LaunchStatus.Invalid: return "nothing that can be started";
                case LaunchStatus.Failed: return "Windows could not start it";
                case LaunchStatus.StartedElevated: return "not restarted: it would have run as administrator";
                default: return status.ToString();
            }
        }

        public static string Describe(RestartResult r) {
            if(r == null)
                return "Graphics apps: no result";
            string text = "Graphics apps: " + (r.Entry?.DisplayName ?? r.AppKey) + ": " + r.Outcome;
            if(r.Outcome == RestartOutcome.NotRestartable)
                text += " (" + Reason(r.Block) + ")";
            if(r.StillRunning.Count > 0)
                text += ", still running: " + string.Join(", ", r.StillRunning.Select(p => p.ToString()));
            if(r.Launches.Count > 0)
                text += ", launch: " + string.Join("; ", r.Launches.Select(l => l.ToString()));
            if(r.Started.Count > 0)
                text += ", new: " + string.Join(", ", r.Started.Select(p => p.ToString()));
            if(r.Detail != null)
                text += " - " + r.Detail;
            return text;
        }

    }
#endregion

}
