  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Management.Infrastructure;
using OmenMon.External;

namespace OmenMon.Library {

    // The Win32 side of restarting Graphics apps (decisions: GraphicsRestart.cs).
    // OmenMon runs elevated; nothing here starts a process any other way than
    // CreateProcessWithTokenW with a copy of the shell's unelevated token.

#region Process handles and tokens
    internal static class ProcessHandles {

        // Opens a process for reading; zero if it cannot be opened
        public static IntPtr Open(int pid, uint access = Kernel32.PROCESS_QUERY_LIMITED_INFORMATION) {
            if(pid <= 0)
                return IntPtr.Zero;
            return Kernel32.OpenProcess(access, false, pid);
        }

        public static void Close(IntPtr handle) {
            if(handle != IntPtr.Zero)
                Kernel32.CloseHandle(handle);
        }

        // FILETIME; 0 if unreadable
        public static long CreationTime(IntPtr process) {
            return Kernel32.GetProcessTimes(process, out long created, out _, out _, out _) ? created : 0;
        }

        public static string ImagePath(IntPtr process) {
            StringBuilder sb = new StringBuilder(1024);
            uint size = (uint) sb.Capacity;
            return Kernel32.QueryFullProcessImageNameW(process, 0, sb, ref size) ? sb.ToString() : null;
        }

        public static string Aumid(IntPtr process) {
            StringBuilder sb = new StringBuilder(256);
            uint length = (uint) sb.Capacity;
            int r = Kernel32.GetApplicationUserModelId(process, ref length, sb);
            if(r == 122 && length > 0 && length < 4096) {   // ERROR_INSUFFICIENT_BUFFER
                sb = new StringBuilder((int) length);
                r = Kernel32.GetApplicationUserModelId(process, ref length, sb);
            }
            return r == 0 && sb.Length > 0 ? sb.ToString() : null;
        }

        // The token of a process, for querying (and duplicating); zero if it cannot be opened
        public static IntPtr Token(IntPtr process, int access = AdvApi32.TOKEN_QUERY) {
            IntPtr token = IntPtr.Zero;
            return AdvApi32.OpenProcessToken(process, access, ref token) != 0 ? token : IntPtr.Zero;
        }

        // TokenElevation; null if it cannot be read
        public static bool? IsElevated(IntPtr token) {
            if(token == IntPtr.Zero)
                return null;
            IntPtr buffer = Marshal.AllocHGlobal(4);
            try {
                if(!AdvApi32.GetTokenInformation(token, AdvApi32.TokenElevation, buffer, 4, out _))
                    return null;
                return Marshal.ReadInt32(buffer) != 0;
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // The token user's SID as a string; null if it cannot be read
        public static string UserSid(IntPtr token) {
            if(token == IntPtr.Zero)
                return null;
            AdvApi32.GetTokenInformation(token, AdvApi32.TokenUser, IntPtr.Zero, 0, out int length);
            if(length <= 0 || length > 4096)
                return null;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try {
                if(!AdvApi32.GetTokenInformation(token, AdvApi32.TokenUser, buffer, length, out _))
                    return null;
                // TOKEN_USER starts with SID_AND_ATTRIBUTES, which starts with the SID pointer
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            } catch {
                return null;
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // Elevation and user of a process's token
        public static void ReadToken(IntPtr process, out bool? elevated, out string sid) {
            elevated = null;
            sid = null;
            IntPtr token = Token(process);
            if(token == IntPtr.Zero)
                return;
            try {
                elevated = IsElevated(token);
                sid = UserSid(token);
            } finally {
                Close(token);
            }
        }

        // The process the shell window (Explorer's desktop) belongs to; 0 if none
        public static int ShellPid() {
            IntPtr window = User32.GetShellWindow();
            if(window == IntPtr.Zero)
                return 0;
            User32.GetWindowThreadProcessId(window, out int pid);
            return pid;
        }

        public static int SessionOf(int pid) {
            return pid > 0 && Kernel32.ProcessIdToSessionId(pid, out int session) ? session : -1;
        }

        // Visible top-level windows that no other window owns (what CloseMainWindow
        // would pick), per process id
        public static Dictionary<int, List<IntPtr>> MainWindows() {
            Dictionary<int, List<IntPtr>> windows = new Dictionary<int, List<IntPtr>>();
            User32.EnumWindows((hWnd, lParam) => {
                if(User32.IsWindowVisible(hWnd) && User32.GetWindow(hWnd, User32.GW_OWNER) == IntPtr.Zero) {
                    User32.GetWindowThreadProcessId(hWnd, out int pid);
                    if(pid > 0) {
                        if(!windows.TryGetValue(pid, out List<IntPtr> list))
                            windows[pid] = list = new List<IntPtr>();
                        list.Add(hWnd);
                    }
                }
                return true;
            }, IntPtr.Zero);
            return windows;
        }

    }
#endregion

#region Catalog
    // The running processes: ids, parents, sessions and command lines from WMI
    // (Win32_Process), the rest from each process and its token.
    //
    // The working directory is not read: what a process has now (in its PEB, through
    // undocumented offsets) need not be what it was started in, so the directory of
    // the executable stands in when the app is started again (WorkingDirectory null).
    public sealed class WindowsProcessCatalog : IProcessCatalog {

        public ProcessSnapshot Snapshot() {
            ProcessSnapshot s = new ProcessSnapshot();
            try {
                using(Process own = Process.GetCurrentProcess())
                    s.OwnPid = own.Id;
            } catch { }

            // The interactive user: the one the shell runs as
            string userSid = null;
            int shellPid = ProcessHandles.ShellPid();
            if(shellPid > 0) {
                s.InteractiveSession = ProcessHandles.SessionOf(shellPid);
                IntPtr h = ProcessHandles.Open(shellPid);
                if(h != IntPtr.Zero)
                    try {
                        ProcessHandles.ReadToken(h, out _, out userSid);
                    } finally {
                        ProcessHandles.Close(h);
                    }
            }

            Dictionary<int, List<IntPtr>> windows;
            try {
                windows = ProcessHandles.MainWindows();
            } catch {
                windows = new Dictionary<int, List<IntPtr>>();
            }

            try {
                using(CimSession cim = CimSession.Create(null))
                    foreach(CimInstance i in cim.QueryInstances(@"root\cimv2", "WQL",
                        "SELECT ProcessId, ParentProcessId, SessionId, CommandLine FROM Win32_Process"))
                        using(i) {
                            ProcessEntry p = new ProcessEntry {
                                Pid = ToInt(i, "ProcessId"),
                                ParentPid = ToInt(i, "ParentProcessId"),
                                SessionId = ToInt(i, "SessionId", -1),
                                CommandLine = i.CimInstanceProperties["CommandLine"]?.Value as string };
                            p.HasVisibleWindow = windows.ContainsKey(p.Pid);
                            Fill(p, userSid);
                            s.Processes.Add(p);
                        }
            } catch {
                return null;
            }
            return s;
        }

        private static int ToInt(CimInstance i, string property, int fallback = 0) {
            try {
                object v = i.CimInstanceProperties[property]?.Value;
                return v == null ? fallback : Convert.ToInt32(v, CultureInfo.InvariantCulture);
            } catch {
                return fallback;
            }
        }

        private static void Fill(ProcessEntry p, string userSid) {
            IntPtr h = ProcessHandles.Open(p.Pid);
            if(h == IntPtr.Zero)
                return;
            try {
                p.CreationTime = ProcessHandles.CreationTime(h);
                p.ImagePath = ProcessHandles.ImagePath(h);
                p.Aumid = ProcessHandles.Aumid(h);
                ProcessHandles.ReadToken(h, out p.Elevated, out string sid);
                if(sid != null && userSid != null)
                    p.InteractiveUser = string.Equals(sid, userSid, StringComparison.OrdinalIgnoreCase);
            } finally {
                ProcessHandles.Close(h);
            }
        }

    }
#endregion

#region Closer
    // Polite closing (WM_CLOSE, posted, so a hung app cannot hold OmenMon up) and,
    // on the user's word only, ending. Each call first checks the pid still names the
    // same process (creation time); one it cannot be sure of is not touched.
    public sealed class WindowsProcessCloser : IProcessCloser {

        private const int ERROR_INVALID_PARAMETER = 87;   // OpenProcess: no such process

        public bool RequestClose(ProcessEntry p) {
            if(p == null || p.CreationTime == 0)
                return false;
            IntPtr h = ProcessHandles.Open(p.Pid);
            if(h == IntPtr.Zero)
                return false;
            try {
                if(ProcessHandles.CreationTime(h) != p.CreationTime)
                    return false;
                if(!ProcessHandles.MainWindows().TryGetValue(p.Pid, out List<IntPtr> windows))
                    return false;
                int posted = 0;
                foreach(IntPtr w in windows)
                    if(User32.PostMessage(w, User32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                        posted++;
                return posted > 0;
            } finally {
                ProcessHandles.Close(h);
            }
        }

        public bool WaitForExit(ProcessEntry p, TimeSpan timeout) {
            if(p == null || p.CreationTime == 0)
                return false;   // cannot tell
            IntPtr h = ProcessHandles.Open(p.Pid, Kernel32.SYNCHRONIZE | Kernel32.PROCESS_QUERY_LIMITED_INFORMATION);
            if(h == IntPtr.Zero)
                return Marshal.GetLastWin32Error() == ERROR_INVALID_PARAMETER;   // gone; else unknown
            try {
                if(ProcessHandles.CreationTime(h) != p.CreationTime)
                    return true;    // the id names another process now: this one has exited
                double ms = Math.Max(0, Math.Min(timeout.TotalMilliseconds, int.MaxValue));
                return Kernel32.WaitForSingleObject(h, (uint) ms) == Kernel32.WAIT_OBJECT_0;
            } finally {
                ProcessHandles.Close(h);
            }
        }

        public bool EndProcess(ProcessEntry p) {
            if(p == null || p.CreationTime == 0)
                return false;
            IntPtr h = ProcessHandles.Open(p.Pid, Kernel32.PROCESS_TERMINATE | Kernel32.PROCESS_QUERY_LIMITED_INFORMATION);
            if(h == IntPtr.Zero)
                return false;
            try {
                if(ProcessHandles.CreationTime(h) != p.CreationTime)
                    return false;
                return Kernel32.TerminateProcess(h, 1);
            } finally {
                ProcessHandles.Close(h);
            }
        }

    }
#endregion

#region Launcher
    // A process started suspended, with its handles
    internal struct LaunchedProcess {
        public IntPtr Process, Thread;
        public int Pid;
    }

    // The Win32 calls the launcher makes, behind a seam so its decisions can be tested
    internal interface ILaunchApi {
        string ExplorerPath { get; }
        int OwnSession();
        // The shell's token (TOKEN_QUERY | TOKEN_DUPLICATE) and session; false if there
        // is no shell window, or its process or token cannot be opened
        bool OpenShellToken(out IntPtr token, out int session);
        bool? IsElevated(IntPtr token);
        // A primary token copied from the given one; zero on failure
        IntPtr DuplicatePrimary(IntPtr token);
        // CreateProcessWithTokenW, suspended, on winsta0\default, with the token's environment
        bool CreateSuspended(IntPtr primaryToken, string imagePath, string commandLine, string workingDirectory,
            out LaunchedProcess process, out int error);
        bool? IsProcessElevated(IntPtr process);
        bool Resume(IntPtr thread);
        bool Terminate(IntPtr process);
        void Close(IntPtr handle);
    }

    // Starts apps unelevated, the one way (DESIGN.md): the shell window -> its process
    // -> its token, refused unless known to be unelevated -> a primary copy ->
    // CreateProcessWithTokenW, suspended. The new process's token is checked before it
    // runs a single instruction: unless known to be unelevated it is ended, not resumed.
    // Packaged apps take the same route, starting explorer.exe shell:AppsFolder\<AUMID>.
    // There is no fallback (Process.Start, ShellExecute, CreateProcess from OmenMon).
    public sealed class UnelevatedLauncher : IUnelevatedLauncher {

        private readonly ILaunchApi api;

        public UnelevatedLauncher() : this(new Win32LaunchApi()) { }

        internal UnelevatedLauncher(ILaunchApi api) {
            this.api = api;
        }

        public LaunchResult Check() {
            IntPtr shell = IntPtr.Zero;
            try {
                return LaunchResult.Of(ShellStatus(out shell));
            } catch(Exception e) {
                return LaunchResult.Of(LaunchStatus.NoShell, e.Message);
            } finally {
                this.api.Close(shell);
            }
        }

        public LaunchResult Start(string imagePath, string commandLine, string workingDirectory) {
            if(string.IsNullOrWhiteSpace(commandLine) && !string.IsNullOrEmpty(imagePath))
                commandLine = LaunchPolicy.Quote(imagePath);
            if(LaunchPolicy.CheckStart(imagePath, commandLine) != LaunchStatus.Ready)
                return LaunchResult.Of(LaunchStatus.Invalid, imagePath);
            return Launch(imagePath, commandLine, workingDirectory);
        }

        public LaunchResult StartPackaged(string aumid) {
            if(!LaunchPolicy.IsValidAumid(aumid))
                return LaunchResult.Of(LaunchStatus.Invalid, aumid);
            string explorer = this.api.ExplorerPath;
            return Launch(explorer, LaunchPolicy.PackagedCommandLine(explorer, aumid), LaunchPolicy.DirectoryOf(explorer));
        }

        private LaunchStatus ShellStatus(out IntPtr shell) {
            bool found = this.api.OpenShellToken(out shell, out int session);
            return LaunchPolicy.CheckShell(found, session, this.api.OwnSession(), found ? this.api.IsElevated(shell) : null);
        }

        private LaunchResult Launch(string imagePath, string commandLine, string workingDirectory) {
            IntPtr shell = IntPtr.Zero, primary = IntPtr.Zero;
            try {
                LaunchStatus status = ShellStatus(out shell);
                if(status != LaunchStatus.Ready)
                    return LaunchResult.Of(status);
                primary = this.api.DuplicatePrimary(shell);
                if(primary == IntPtr.Zero)
                    return LaunchResult.Of(LaunchStatus.Failed, "the shell's token could not be copied");
                // The copy is the token the app gets: checked as well
                if(this.api.IsElevated(primary) != false)
                    return LaunchResult.Of(LaunchStatus.ShellElevated);

                if(!this.api.CreateSuspended(primary, imagePath, commandLine, workingDirectory, out LaunchedProcess p, out int error))
                    return LaunchResult.Of(LaunchStatus.Failed, imagePath, 0, error);
                try {
                    if(LaunchPolicy.MustEnd(this.api.IsProcessElevated(p.Process))) {
                        this.api.Terminate(p.Process);
                        return LaunchResult.Of(LaunchStatus.StartedElevated, imagePath, p.Pid);
                    }
                    if(!this.api.Resume(p.Thread)) {
                        this.api.Terminate(p.Process);
                        return LaunchResult.Of(LaunchStatus.Failed, "it could not be resumed", p.Pid);
                    }
                    return LaunchResult.Of(LaunchStatus.Started, imagePath, p.Pid);
                } finally {
                    this.api.Close(p.Thread);
                    this.api.Close(p.Process);
                }
            } catch(Exception e) {
                return LaunchResult.Of(LaunchStatus.Failed, e.Message);
            } finally {
                this.api.Close(primary);
                this.api.Close(shell);
            }
        }

    }

    internal sealed class Win32LaunchApi : ILaunchApi {

        private const string Desktop = @"winsta0\default";

        public string ExplorerPath {
            get {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return Path.Combine(string.IsNullOrEmpty(windows) ? @"C:\Windows" : windows, "explorer.exe");
            }
        }

        public int OwnSession() {
            using(Process own = Process.GetCurrentProcess())
                return own.SessionId;
        }

        public bool OpenShellToken(out IntPtr token, out int session) {
            token = IntPtr.Zero;
            session = -1;
            int pid = ProcessHandles.ShellPid();
            if(pid <= 0)
                return false;
            session = ProcessHandles.SessionOf(pid);
            IntPtr h = ProcessHandles.Open(pid);
            if(h == IntPtr.Zero)
                return false;
            try {
                token = ProcessHandles.Token(h, AdvApi32.TOKEN_QUERY | AdvApi32.TOKEN_DUPLICATE);
                return token != IntPtr.Zero;
            } finally {
                ProcessHandles.Close(h);
            }
        }

        public bool? IsElevated(IntPtr token) {
            return ProcessHandles.IsElevated(token);
        }

        public IntPtr DuplicatePrimary(IntPtr token) {
            const int access = AdvApi32.TOKEN_QUERY | AdvApi32.TOKEN_DUPLICATE | AdvApi32.TOKEN_ASSIGN_PRIMARY
                | AdvApi32.TOKEN_ADJUST_DEFAULT | AdvApi32.TOKEN_ADJUST_SESSIONID;
            return AdvApi32.DuplicateTokenEx(token, access, IntPtr.Zero, AdvApi32.SecurityImpersonation, AdvApi32.TokenPrimary, out IntPtr copy)
                ? copy : IntPtr.Zero;
        }

        public bool CreateSuspended(IntPtr primaryToken, string imagePath, string commandLine, string workingDirectory,
            out LaunchedProcess process, out int error) {
            process = new LaunchedProcess();
            error = 0;
            // A directory that is gone would fail the start: the executable's stands in
            if(string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
                workingDirectory = LaunchPolicy.DirectoryOf(imagePath);
            IntPtr environment = IntPtr.Zero;
            try {
                uint flags = AdvApi32.CREATE_SUSPENDED;
                if(UserEnv.CreateEnvironmentBlock(out environment, primaryToken, false))
                    flags |= AdvApi32.CREATE_UNICODE_ENVIRONMENT;
                else
                    environment = IntPtr.Zero;
                AdvApi32.STARTUPINFO si = new AdvApi32.STARTUPINFO { lpDesktop = Desktop };
                si.cb = Marshal.SizeOf(si);
                StringBuilder line = new StringBuilder(commandLine, commandLine.Length + 1);
                if(!AdvApi32.CreateProcessWithTokenW(primaryToken, 0, imagePath, line, flags, environment, workingDirectory,
                    ref si, out AdvApi32.PROCESS_INFORMATION pi)) {
                    error = Marshal.GetLastWin32Error();
                    return false;
                }
                process = new LaunchedProcess { Process = pi.hProcess, Thread = pi.hThread, Pid = pi.dwProcessId };
                return true;
            } finally {
                if(environment != IntPtr.Zero)
                    UserEnv.DestroyEnvironmentBlock(environment);
            }
        }

        public bool? IsProcessElevated(IntPtr process) {
            IntPtr token = ProcessHandles.Token(process);
            try {
                return ProcessHandles.IsElevated(token);
            } finally {
                ProcessHandles.Close(token);
            }
        }

        public bool Resume(IntPtr thread) {
            return thread != IntPtr.Zero && Kernel32.ResumeThread(thread) != uint.MaxValue;
        }

        public bool Terminate(IntPtr process) {
            return process != IntPtr.Zero && Kernel32.TerminateProcess(process, 1);
        }

        public void Close(IntPtr handle) {
            ProcessHandles.Close(handle);
        }

    }
#endregion

#region Self-test
    // A manual check of the launch route on the real system, for a hardware/UAC test
    // run: needs OmenMon's elevation, touches no hardware. Not reachable from the CLI
    // or the GUI. It starts an app (System32\notepad.exe unless another exe is given)
    // through UnelevatedLauncher, logs the new pid and the elevation of every new
    // process with that exe's file name, waits 3 s and closes those politely
    // (WM_CLOSE; never ended). Processes that ran before are not touched. On Windows
    // 11, System32\notepad.exe hands over to the packaged Notepad and exits, so the
    // new process to look at is that one; Notepad may also ask to save restored tabs,
    // so an app without a save prompt (System32\charmap.exe) tests the close better.
    //
    // From an elevated Windows PowerShell 5.1 (it runs on .NET Framework, so it loads
    // the net48 build):
    //   $a = [Reflection.Assembly]::LoadFrom('<repo>\Bin\OmenMon.exe')
    //   $t = $a.GetType('OmenMon.Library.GraphicsRestartSelfTest')
    //   $m = $t.GetMethod('Run', [Reflection.BindingFlags]'NonPublic, Static', $null, [type[]]@([string], [string]), $null)
    //   $m.Invoke($null, [object[]]@([string]'C:\Users\maroa\AppData\Local\Temp\graphics-selftest.log', 'C:\Windows\System32\charmap.exe'))
    // (the casts matter: a path from Join-Path is a PSObject, which Invoke cannot pass)
    // Expected: "own token elevated: True", "check: Ready", "launch: Started pid N",
    // the new process with "elevated False", the windows asked to close, then "closed
    // after n s" (up to 20 s).
    internal static class GraphicsRestartSelfTest {

        internal static void Run(string logPath) {
            Run(logPath, null);
        }

        internal static void Run(string logPath, string exePath) {
            void Log(string text) {
                try {
                    File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        + "  " + text + Environment.NewLine);
                } catch { }
            }

            try {
                Log("Graphics restart self-test");
                using(Process own = Process.GetCurrentProcess()) {
                    bool? elevated;
                    IntPtr token = ProcessHandles.Token(own.Handle);
                    try {
                        elevated = ProcessHandles.IsElevated(token);
                    } finally {
                        ProcessHandles.Close(token);
                    }
                    Log("own pid " + own.Id + ", session " + own.SessionId + ", own token elevated: " + (elevated?.ToString() ?? "unknown"));
                }

                WindowsProcessCatalog catalog = new WindowsProcessCatalog();
                WindowsProcessCloser closer = new WindowsProcessCloser();
                UnelevatedLauncher launcher = new UnelevatedLauncher();

                LaunchResult check = launcher.Check();
                Log("check: " + check);
                if(check.Status != LaunchStatus.Ready)
                    return;

                ProcessSnapshot before = catalog.Snapshot();
                if(before == null) {
                    Log("the running processes cannot be listed: stopped");
                    return;
                }
                HashSet<string> old = new HashSet<string>(before.Processes.Select(p => p.Pid + ":" + p.CreationTime));

                string exe = exePath;
                if(string.IsNullOrEmpty(exe)) {
                    string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    exe = Path.Combine(string.IsNullOrEmpty(windows) ? @"C:\Windows" : windows, "System32", "notepad.exe");
                }
                string name = Path.GetFileName(exe);
                LaunchResult r = launcher.Start(exe, null, LaunchPolicy.DirectoryOf(exe));
                Log("launch: " + r);
                if(r.Status != LaunchStatus.Started)
                    return;

                Thread.Sleep(3000);
                ProcessSnapshot after = catalog.Snapshot();
                List<ProcessEntry> fresh = after?.Processes.Where(p => !old.Contains(p.Pid + ":" + p.CreationTime)
                    && p.ImagePath != null && string.Equals(Path.GetFileName(p.ImagePath), name, StringComparison.OrdinalIgnoreCase)).ToList()
                    ?? new List<ProcessEntry>();
                if(fresh.Count == 0)
                    Log("no new " + name + " process (a running one may have opened the window): close it by hand");
                foreach(ProcessEntry p in fresh) {
                    Log("new: " + p + " " + p.ImagePath + (p.Aumid != null ? " [" + p.Aumid + "]" : "")
                        + ", parent " + p.ParentPid + ", session " + p.SessionId
                        + ", elevated " + (p.Elevated?.ToString() ?? "unknown")
                        + ", interactive user " + (p.InteractiveUser?.ToString() ?? "unknown")
                        + ", window " + p.HasVisibleWindow);
                    // Which windows get WM_CLOSE, and how long the app takes to exit
                    if(ProcessHandles.MainWindows().TryGetValue(p.Pid, out List<IntPtr> targets))
                        foreach(IntPtr w in targets) {
                            char[] title = new char[256];
                            int n = User32.GetWindowText(w, title, title.Length);
                            Log("window 0x" + w.ToString("X") + " \"" + new string(title, 0, Math.Max(0, n)) + "\"");
                        }
                    Stopwatch watch = Stopwatch.StartNew();
                    bool asked = closer.RequestClose(p);
                    bool exited = asked && closer.WaitForExit(p, TimeSpan.FromSeconds(20));
                    Log(p + ": " + (exited ? "closed after " + watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
                        : asked ? "asked to close, still running after 20 s: close it by hand" : "no window to close: close it by hand"));
                }
            } catch(Exception e) {
                Log("failed: " + e);
            }
        }

    }
#endregion

}
